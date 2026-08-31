using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Security;

namespace OpenShell.Providers.Remote;

/// <summary>
/// 默认的 <see cref="ICredentialProvider"/> 实现: 从 <c>~/.openshell/sftp-credentials.json</c>
/// 加载非敏感元数据到内存，并把 password/private-key passphrase 交给加密秘密存储。
/// </summary>
/// <remarks>
/// 凭据文件格式 (JSON 数组):
/// <code>
/// [
///   {
///     "host": "example.com",
///     "user": "alice",
///     "port": 22,
///     "passwordSecretKey": "sftp/example.com/alice/password",
///     "privateKeyPath": null,
///     "privateKeyPassphraseSecretKey": null
///   }
/// ]
/// </code>
/// 文件不存在时返回空列表 (用户通过 set-sftpcredential 命令配置); 之后自动创建并写入。
/// </remarks>
public sealed class InMemoryCredentialProvider : ICredentialProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ISecretStore _secretStore;
    private readonly List<SftpCredentials> _creds = new();
    private readonly object _lock = new();

    /// <summary>使用默认路径 <c>~/.openshell/sftp-credentials.json</c>。</summary>
    public InMemoryCredentialProvider() : this(GetDefaultFilePath(), null) { }

    /// <summary>使用指定路径, 主要用于测试。</summary>
    public InMemoryCredentialProvider(string filePath, ISecretStore? secretStore = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        // IH-012: macOS 用 Keychain, Windows 用 DPAPI 文件, Linux 用 0600 受保护文件。
        _secretStore = secretStore ?? OpenShell.Security.SecretStoreFactory.CreateDefault(filePath + ".secrets");
        TryLoadFromFile();
    }

    /// <summary>最近一次加载/持久化错误，不包含秘密值。启动可继续时由宿主展示此状态。</summary>
    public string? LastPersistenceError { get; private set; }

    /// <inheritdoc />
    public SftpCredentials? GetCredentials(string host, string user)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(user);
        lock (_lock)
        {
            return _creds.FirstOrDefault(c =>
                string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>列出所有已配置的凭据 (按 host 排序, 不含 password 字段值, 仅保留 HasPassword 标志)。</summary>
    public IReadOnlyList<SftpCredentials> ListCredentials(string? hostFilter = null)
    {
        lock (_lock)
        {
            var query = _creds.AsEnumerable();
            if (!string.IsNullOrEmpty(hostFilter))
                query = query.Where(c => string.Equals(c.Host, hostFilter, StringComparison.OrdinalIgnoreCase));
            return query
                .OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.User, StringComparer.OrdinalIgnoreCase)
                .Select(c => c with { Password = c.Password is null ? null : "****" })
                .ToImmutableList();
        }
    }

    /// <summary>
    /// 新增或更新凭据 (按 host + user 主键去重)。修改后立即持久化到磁盘。
    /// </summary>
    public void SetCredentials(SftpCredentials cred)
    {
        ArgumentNullException.ThrowIfNull(cred);
        lock (_lock)
        {
            // 先保留旧值, 失败时同时恢复内存索引和秘密引用。
            var previous = _creds.Where(c =>
                string.Equals(c.Host, cred.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.User, cred.User, StringComparison.OrdinalIgnoreCase)).ToList();
            _creds.RemoveAll(c => previous.Contains(c));
            _creds.Add(cred);
            try
            {
                PersistSecrets(cred);
                SaveToFile();
            }
            catch
            {
                _creds.RemoveAll(c => ReferenceEquals(c, cred));
                foreach (var old in previous)
                {
                    _creds.Add(old);
                    try { PersistSecrets(old); } catch { /* best-effort rollback */ }
                }
                try { SaveToFile(); } catch { /* 保留原始异常, 但不再继续破坏内存状态 */ }
                throw;
            }
        }
    }

    /// <summary>
    /// 删除凭据。返回是否删除成功。
    /// </summary>
    public bool RemoveCredentials(string host, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_lock)
        {
            var removedCredentials = _creds.Where(c =>
                string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
                && (user is null || string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase))).ToList();
            var removed = removedCredentials.Count;
            if (removed > 0)
            {
                _creds.RemoveAll(c => removedCredentials.Contains(c));
                try
                {
                    foreach (var cred in removedCredentials)
                        RemoveSecrets(cred);
                    SaveToFile();
                }
                catch
                {
                    foreach (var cred in removedCredentials)
                    {
                        _creds.Add(cred);
                        try { PersistSecrets(cred); } catch { /* best-effort rollback */ }
                    }
                    try { SaveToFile(); } catch { /* 保留原始异常 */ }
                    throw;
                }
            }
            return removed > 0;
        }
    }

    // ---- 内部: 文件 IO ----

    private void TryLoadFromFile()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var list = JsonSerializer.Deserialize<List<CredRecord>>(json, JsonOpts);
            if (list is null)
                return;

            lock (_lock)
            {
                _creds.Clear();
                var migrated = false;
                foreach (var r in list)
                {
                    if (string.IsNullOrEmpty(r.Host) || string.IsNullOrEmpty(r.User))
                        continue;   // 跳过无效条目
                    var secretBase = r.SecretKey ?? BuildSecretKey(r.Host, r.User);
                    var passwordKey = r.PasswordSecretKey ?? secretBase + "/password";
                    var passphraseKey = r.PrivateKeyPassphraseSecretKey ?? secretBase + "/private-key-passphrase";
                    var password = r.Password;
                    var passphrase = r.PrivateKeyPassphrase;
                    if (password is not null)
                    {
                        _secretStore.SetSecret(passwordKey, password);
                        migrated = true;
                    }
                    else
                    {
                        password = _secretStore.GetSecret(passwordKey);
                    }
                    if (passphrase is not null)
                    {
                        _secretStore.SetSecret(passphraseKey, passphrase);
                        migrated = true;
                    }
                    else
                    {
                        passphrase = _secretStore.GetSecret(passphraseKey);
                    }
                    _creds.Add(new SftpCredentials
                    {
                        Host = r.Host,
                        User = r.User,
                        Port = r.Port <= 0 ? 22 : r.Port,
                        Password = password,
                        PrivateKeyPath = r.PrivateKeyPath,
                        PrivateKeyPassphrase = passphrase,
                    });
                }
                if (migrated)
                    SaveToFile();
            }
        }
        catch (Exception ex)
        {
            // 保持启动可用，但把错误暴露给宿主，不能静默伪装成“没有凭据”。
            LastPersistenceError = $"Credential store load failed: {ex.Message}";
            lock (_lock) _creds.Clear();
        }
    }

    private void SaveToFile()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var list = _creds.Select(c =>
        {
            var secretBase = BuildSecretKey(c.Host, c.User);
            return new CredRecord
            {
                Host = c.Host,
                User = c.User,
                Port = c.Port,
                PrivateKeyPath = c.PrivateKeyPath,
                SecretKey = secretBase,
                PasswordSecretKey = c.Password is null ? null : secretBase + "/password",
                PrivateKeyPassphraseSecretKey = c.PrivateKeyPassphrase is null ? null : secretBase + "/private-key-passphrase",
            };
        }).ToList();

        var json = JsonSerializer.Serialize(list, JsonOpts);
        var tmp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else
                File.Move(tmp, _filePath);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            LastPersistenceError = null;
        }
        catch (Exception ex)
        {
            LastPersistenceError = $"Credential store save failed: {ex.Message}";
            throw;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private void PersistSecrets(SftpCredentials cred)
    {
        var secretBase = BuildSecretKey(cred.Host, cred.User);
        if (cred.Password is null) _secretStore.RemoveSecret(secretBase + "/password");
        else _secretStore.SetSecret(secretBase + "/password", cred.Password);
        if (cred.PrivateKeyPassphrase is null) _secretStore.RemoveSecret(secretBase + "/private-key-passphrase");
        else _secretStore.SetSecret(secretBase + "/private-key-passphrase", cred.PrivateKeyPassphrase);
    }

    private void RemoveSecrets(SftpCredentials cred)
    {
        var secretBase = BuildSecretKey(cred.Host, cred.User);
        _secretStore.RemoveSecret(secretBase + "/password");
        _secretStore.RemoveSecret(secretBase + "/private-key-passphrase");
    }

    private static string BuildSecretKey(string host, string user)
        => $"sftp/{host}/{user}";

    private static string GetDefaultFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".openshell", "sftp-credentials.json");
    }

    /// <summary>JSON 序列化 DTO, 与 <see cref="SftpCredentials"/> 字段对应 (camelCase)。</summary>
    private sealed record CredRecord
    {
        public string Host { get; init; } = "";
        public string User { get; init; } = "";
        public int Port { get; init; } = 22;
        // 仅为旧版明文文件迁移保留；新文件不会序列化此字段。
        public string? Password { get; init; }
        public string? PrivateKeyPath { get; init; }
        public string? PrivateKeyPassphrase { get; init; }
        public string? SecretKey { get; init; }
        public string? PasswordSecretKey { get; init; }
        public string? PrivateKeyPassphraseSecretKey { get; init; }
    }
}
