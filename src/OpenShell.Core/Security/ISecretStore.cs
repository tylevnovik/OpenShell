using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShell.Security;

/// <summary>
/// 小型秘密存储抽象。调用方只通过逻辑 key 读写秘密，不接触序列化格式。
/// </summary>
public interface ISecretStore
{
    string? GetSecret(string key);

    void SetSecret(string key, string value);

    void RemoveSecret(string key);
}

/// <summary>测试与短生命周期场景使用的内存秘密存储。</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? GetSecret(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    public void SetSecret(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
    }

    public void RemoveSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _values.Remove(key);
    }
}

/// <summary>
/// 加密文件秘密存储：Windows 使用当前用户 DPAPI，Unix 使用 0600 权限的随机密钥 + AES-GCM。
/// 这是跨平台安全降级实现；有原生 Keychain/Secret Service 的宿主可替换此接口实现。
/// </summary>
public sealed class ProtectedFileSecretStore : ISecretStore
{
    private const int AesKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly string _keyPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _encryptedValues;

    public ProtectedFileSecretStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _keyPath = filePath + ".key";
        _encryptedValues = Load();
    }

    public string? GetSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_lock)
        {
            return _encryptedValues.TryGetValue(key, out var encoded)
                ? Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(encoded)))
                : null;
        }
    }

    public void SetSecret(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            var previous = _encryptedValues.TryGetValue(key, out var old) ? old : null;
            _encryptedValues[key] = Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(value)));
            try
            {
                Save();
            }
            catch
            {
                if (previous is null) _encryptedValues.Remove(key);
                else _encryptedValues[key] = previous;
                throw;
            }
        }
    }

    public void RemoveSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_lock)
        {
            if (!_encryptedValues.Remove(key)) return;
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var model = JsonSerializer.Deserialize<SecretFile>(File.ReadAllText(_filePath), JsonOptions);
            return model?.Values is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(model.Values, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Secret store '{_filePath}' is corrupted.", ex);
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var model = new SecretFile { Values = new Dictionary<string, string>(_encryptedValues) };
        var json = JsonSerializer.Serialize(model, JsonOptions);
        var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            ReplaceFile(tempPath, _filePath);
            SetOwnerOnlyPermissions(_filePath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        if (OperatingSystem.IsWindows())
            return ProtectWithDpapi(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(GetUnixKey(), TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. tag, .. ciphertext];
    }

    private byte[] Decrypt(byte[] encoded)
    {
        if (OperatingSystem.IsWindows())
            return UnprotectWithDpapi(encoded);

        if (encoded.Length < NonceLength + TagLength)
            throw new InvalidDataException("Encrypted secret payload is too short.");
        var nonce = encoded.AsSpan(0, NonceLength);
        var tag = encoded.AsSpan(NonceLength, TagLength);
        var ciphertext = encoded.AsSpan(NonceLength + TagLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(GetUnixKey(), TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private byte[] GetUnixKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == AesKeyLength) return existing;
            throw new InvalidDataException($"Secret store key '{_keyPath}' has an invalid length.");
        }

        var directory = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(AesKeyLength);
        try
        {
            using var stream = new FileStream(_keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(key);
            stream.Flush(true);
            SetOwnerOnlyPermissions(_keyPath);
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            CryptographicOperations.ZeroMemory(key);
            return GetUnixKey();
        }
        return key;
    }

    private static void ReplaceFile(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null);
        }
        else
        {
            File.Move(source, destination, overwrite: true);
        }
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static byte[] ProtectWithDpapi(byte[] plaintext)
    {
        var input = new DataBlob(plaintext);
        try
        {
            if (!CryptProtectData(ref input.Blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI encryption failed.");
            try { return output.ToArray(); }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally { input.Dispose(); }
    }

    private static byte[] UnprotectWithDpapi(byte[] encrypted)
    {
        var input = new DataBlob(encrypted);
        try
        {
            if (!CryptUnprotectData(ref input.Blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI decryption failed.");
            try { return output.ToArray(); }
            finally { if (output.pbData != IntPtr.Zero) LocalFree(output.pbData); }
        }
        finally { input.Dispose(); }
    }

    private sealed class DataBlob : IDisposable
    {
        public DATA_BLOB Blob;

        public DataBlob(byte[] bytes)
        {
            Blob.cbData = bytes.Length;
            Blob.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Blob.pbData, bytes.Length);
        }

        public void Dispose()
        {
            if (Blob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Blob.pbData);
                Blob.pbData = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;

        public readonly byte[] ToArray()
        {
            var bytes = new byte[cbData];
            Marshal.Copy(pbData, bytes, 0, cbData);
            return bytes;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private sealed class SecretFile
    {
        [JsonPropertyName("values")]
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    }
}

