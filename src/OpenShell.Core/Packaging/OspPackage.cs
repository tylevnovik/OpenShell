using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenShell.Providers;

namespace OpenShell.Packaging;

/// <summary>
/// OpenShell Provider Package (<c>.osp</c>) 模型。Per ADR-0039 §1 / §10.
/// 一个 <c>.osp</c> 包实质是 ZIP 压缩文件, 扩展名 <c>.osp</c>, 内含:
/// <list type="bullet">
///   <item><c>openshell.provider.json</c> — Provider 清单 (ADR-0038 §4 / ADR-0039 §2)</item>
///   <item><c>signature.sig</c> / <c>signature.pub</c> — 签名 (detached, ADR-0039 §8)</item>
///   <item><c>*.dll</c> — 实现程序集</item>
///   <item><c>*.deps.json</c> — .NET 依赖描述</item>
///   <item><c>assets/</c> — 图标与本地化资源</item>
/// </list>
/// 本类型提供打开 / 解包 / 打包三类操作。打包见 <see cref="OspPackager"/>。
/// </summary>
public sealed class OspPackage : IDisposable, IAsyncDisposable
{
    /// <summary>包内清单文件名约定。Per ADR-0039 §2.</summary>
    public const string ManifestEntryName = "openshell.provider.json";

    /// <summary>包内 detached 签名文件名约定。Per ADR-0039 §8.</summary>
    public const string SignatureEntryName = "signature.sig";

    /// <summary>包内签名公钥文件名约定。Per ADR-0039 §8.</summary>
    public const string PublicKeyEntryName = "signature.pub";

    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private bool _disposed;

    private OspPackage(FileStream stream, ZipArchive archive)
    {
        _stream = stream;
        _archive = archive;
    }

    /// <summary>包文件绝对路径。</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>
    /// 以只读方式打开一个已存在的 <c>.osp</c> 包。Per ADR-0039 §1.
    /// 调用方负责 <see cref="Dispose"/> 释放底层 ZIP 句柄。
    /// </summary>
    /// <exception cref="OspPackageException">文件不存在或不是合法 ZIP。</exception>
    public static async Task<OspPackage> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new OspPackageException($"Package file not found: {path}");

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var pkg = new OspPackage(stream, archive) { Path = path };

            // 校验包内必须包含 manifest。
            if (archive.GetEntry(ManifestEntryName) is null)
                throw new OspPackageException($"Package '{path}' is missing '{ManifestEntryName}'.");

            await Task.CompletedTask.ConfigureAwait(false);
            return pkg;
        }
        catch (OspPackageException) { throw; }
        catch (Exception ex)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            throw new OspPackageException($"Failed to open package '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>返回底层 <see cref="ZipArchive"/> (只读), 供调用方枚举 entries / 读取签名等。</summary>
    public ZipArchive GetArchive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _archive;
    }

    /// <summary>
    /// 从包内读取并解析 <see cref="ProviderManifest"/>。Per ADR-0039 §2.
    /// 多次调用会重复解析; 调用方应缓存结果。
    /// </summary>
    /// <exception cref="OspPackageException">manifest 缺失或非法。</exception>
    public Task<ProviderManifest> ReadManifestAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = _archive.GetEntry(ManifestEntryName)
            ?? throw new OspPackageException($"Package '{Path}' is missing '{ManifestEntryName}'.");
        using var es = entry.Open();
        using var sr = new StreamReader(es);
        var json = sr.ReadToEnd();
        try
        {
            return Task.FromResult(ProviderManifest.Parse(json));
        }
        catch (ProviderManifestException ex)
        {
            throw new OspPackageException($"Invalid manifest in package '{Path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 读取 detached 签名与公钥 (若存在)。Per ADR-0039 §8.
    /// 未签名包返回 <c>(null, null)</c>。
    /// </summary>
    public (byte[]? Signature, byte[]? PublicKey) ReadSignature()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? sig = null, pub = null;
        if (_archive.GetEntry(SignatureEntryName) is { } sigEntry)
        {
            using var es = sigEntry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            sig = ms.ToArray();
        }
        if (_archive.GetEntry(PublicKeyEntryName) is { } pubEntry)
        {
            using var es = pubEntry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            pub = ms.ToArray();
        }
        return (sig, pub);
    }

    /// <summary>
    /// 计算签名载荷哈希。Per ADR-0039 §8.
    /// 载荷 = manifest JSON + "\n" + 每个 .dll 的 "entryName=SHA256\n"。
    /// 返回 SHA256(payload)。签名方与校验方共用此方法保证哈希一致。
    /// </summary>
    /// <param name="manifest">已解析的清单对象 (会重新序列化为规范 JSON)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>32 字节 SHA256 哈希。</returns>
    public async Task<byte[]> ComputePayloadHashAsync(ProviderManifest manifest, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);

        using var sha = SHA256.Create();
        using var ms = new MemoryStream();

        // 1) manifest JSON (规范序列化, 与 OspPackager.SignAsync 一致)。
        await using (var sw = new StreamWriter(ms, leaveOpen: true))
        {
            await sw.WriteAsync(System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJsonOptions.Default).AsMemory(), ct).ConfigureAwait(false);
            await sw.WriteLineAsync().ConfigureAwait(false);
        }

        // 2) 每个 .dll 的 SHA256。
        foreach (var entry in _archive.Entries)
        {
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            using var es = entry.Open();
            var hash = await sha.ComputeHashAsync(es, ct).ConfigureAwait(false);
            await ms.WriteAsync(System.Text.Encoding.ASCII.GetBytes(entry.Name).AsMemory(), ct).ConfigureAwait(false);
            await ms.WriteAsync(System.Text.Encoding.ASCII.GetBytes("=").AsMemory(), ct).ConfigureAwait(false);
            await ms.WriteAsync(System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(hash)).AsMemory(), ct).ConfigureAwait(false);
            await ms.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), ct).ConfigureAwait(false);
        }

        ms.Position = 0;
        return await sha.ComputeHashAsync(ms, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 解包到目标目录。Per ADR-0039 §6.
    /// 目标目录会被创建 (含父目录)。已存在的同名文件会被覆盖。
    /// </summary>
    /// <param name="targetDir">解包目标目录绝对路径。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task ExtractToAsync(string targetDir, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(targetDir);
        Directory.CreateDirectory(targetDir);

        // 禁止包内 entry 含绝对路径或 .. 跨越 (ADR-0039 约束: 包内文件禁止绝对路径)。
        foreach (var entry in _archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var full = System.IO.Path.Combine(targetDir, entry.FullName.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var resolved = System.IO.Path.GetFullPath(full);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(targetDir) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resolved, System.IO.Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            {
                throw new OspPackageException($"Package '{Path}' contains an entry escaping the target directory: '{entry.FullName}'.");
            }

            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(resolved);
                continue;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resolved)!);
            using var es = entry.Open();
            using var fs = new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
            await es.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 创建一个 <c>.osp</c> 包。Per ADR-0039 §1 / §10.
    /// 写入 manifest + 所有指定文件 (DLL/deps/assets 等) 到 ZIP。不写签名 (签名见 OspPackager.SignAsync)。
    /// </summary>
    /// <param name="manifest">Provider 清单 (已 Validate)。</param>
    /// <param name="filePaths">要打包进包的文件绝对路径列表 (DLL/deps.json/pdb/assets 等)。</param>
    /// <param name="outputDir">输出目录, 函数会创建该目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>生成的 <c>.osp</c> 文件绝对路径。</returns>
    public static async Task<string> CreateAsync(ProviderManifest manifest, IReadOnlyList<string> filePaths, string outputDir, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(outputDir);
        manifest.Validate();

        Directory.CreateDirectory(outputDir);
        var fileName = $"{SanitiseName(manifest.Name)}-{manifest.Version}.osp";
        var outPath = System.IO.Path.Combine(outputDir, fileName);

        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        // 1) manifest。
        var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using (var es = manifestEntry.Open())
        {
            var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions.Default);
            await using var sw = new StreamWriter(es, leaveOpen: false);
            await sw.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }

        // 2) 用户文件 (按文件名扁平化加入, 子目录保留相对结构)。
        foreach (var fp in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(fp))
                throw new OspPackageException($"File to pack not found: {fp}");
            var entryName = System.IO.Path.GetFileName(fp);
            // 简化: 全部扁平化到包根 (assets/ 子目录由调用方自行指定 entry 名, 暂不实现)。
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var es = entry.Open();
            await using var src = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
            await src.CopyToAsync(es, ct).ConfigureAwait(false);
        }

        return outPath;
    }

    /// <summary>把包名转换为文件名安全形式 (替换非法字符)。</summary>
    private static string SanitiseName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "package";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _archive.Dispose(); } catch { /* best-effort */ }
        try { _stream.Dispose(); } catch { /* best-effort */ }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _archive.Dispose(); } catch { /* best-effort */ }
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
    }
}
