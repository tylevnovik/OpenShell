using System.Security.Cryptography;
using OpenShell.Packaging.Signing;
using OpenShell.Providers;

namespace OpenShell.Packaging;

/// <summary>
/// 把 Provider 程序集 + manifest 打包成 <c>.osp</c> 包。Per ADR-0039 §1 / §10.
/// 提供打包 (pack) 与签名 (sign) 两个职责, 供 <c>Publish-Provider</c> 命令与
/// <c>dotnet openshell pack</c> 全局工具调用。
/// </summary>
public sealed class OspPackager
{
    private readonly ISignatureVerifier? _signatureVerifier;

    public OspPackager(ISignatureVerifier? signatureVerifier = null)
    {
        _signatureVerifier = signatureVerifier;
    }

    /// <summary>
    /// 把一个 Provider 程序集 + manifest 打包成 <c>.osp</c> 文件。Per ADR-0039 §1 / §10.
    /// </summary>
    /// <param name="manifest">Provider 清单 (调用前已 Validate)。</param>
    /// <param name="assemblyPath">Provider 主程序集绝对路径 (DLL)。</param>
    /// <param name="extraFilePaths">附加文件: deps.json / pdb / assets 等 (绝对路径)。</param>
    /// <param name="outputDir">输出目录 (函数会创建)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>生成的 <c>.osp</c> 文件绝对路径。</returns>
    public Task<string> PackAsync(
        ProviderManifest manifest,
        string assemblyPath,
        IReadOnlyList<string>? extraFilePaths = null,
        string? outputDir = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assemblyPath);
        if (!File.Exists(assemblyPath))
            throw new OspPackageException($"Provider assembly not found: {assemblyPath}");

        var files = new List<string> { assemblyPath };
        if (extraFilePaths is not null) files.AddRange(extraFilePaths);

        // 自动推断 deps.json / pdb (与 DLL 同目录, 同名不同扩展)。
        var dir = System.IO.Path.GetDirectoryName(assemblyPath);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(assemblyPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var deps = System.IO.Path.Combine(dir, baseName + ".deps.json");
            if (File.Exists(deps) && !files.Contains(deps, StringComparer.OrdinalIgnoreCase)) files.Add(deps);
            var pdb = System.IO.Path.Combine(dir, baseName + ".pdb");
            if (File.Exists(pdb) && !files.Contains(pdb, StringComparer.OrdinalIgnoreCase)) files.Add(pdb);
        }

        outputDir ??= System.IO.Path.GetDirectoryName(assemblyPath) ?? Environment.CurrentDirectory;
        return OspPackage.CreateAsync(manifest, files, outputDir, ct);
    }

    /// <summary>
    /// 为一个已存在的 <c>.osp</c> 包追加 detached RSA-SHA256 签名。Per ADR-0039 §8 (legacy).
    /// <b>已废弃</b>: 请使用 <see cref="SignEd25519Async"/> 或 <see cref="SignAsync(string, byte[], CancellationToken)"/> 重载。
    /// 保留此方法仅为向后兼容旧版发布脚本; 后续 milestone 将删除。
    /// </summary>
    /// <param name="packagePath">已打包的 <c>.osp</c> 文件绝对路径。</param>
    /// <param name="privateKeyXml">RSA 私钥 (XML 字符串, 由开发者持有)。</param>
    /// <param name="ct">取消令牌。</param>
    [Obsolete("Use the Ed25519 overload SignAsync(string, byte[], CancellationToken) instead. RSA-SHA256 will be removed in a future release.")]
    public async Task SignAsync(string packagePath, string privateKeyXml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        ArgumentNullException.ThrowIfNull(privateKeyXml);
        if (!File.Exists(packagePath))
            throw new OspPackageException($"Package not found: {packagePath}");

        // 1) 读出 manifest + DLL 摘要。
        await using var pkg = await OspPackage.OpenAsync(packagePath, ct).ConfigureAwait(false);
        var manifest = await pkg.ReadManifestAsync(ct).ConfigureAwait(false);
        var archive = pkg.GetArchive();

        // 构造待签名摘要: manifest JSON + 每个 .dll 的 SHA256。
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        await using (var sw = new StreamWriter(ms, leaveOpen: true))
        {
            await sw.WriteAsync(System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJsonOptions.Default).AsMemory(), ct).ConfigureAwait(false);
            await sw.WriteLineAsync().ConfigureAwait(false);
        }
        foreach (var entry in archive.Entries)
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
        var payloadHash = await sha.ComputeHashAsync(ms, ct).ConfigureAwait(false);

        // 2) 用 RSA-SHA256 签名。
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        var signature = rsa.SignHash(payloadHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var pubKey = rsa.ExportSubjectPublicKeyInfo();

        // 3) 写回 signature.sig / signature.pub 到包。
        // ZipArchive 不支持向已存在包追加 entry (Read 模式), 需重建一个临时副本再替换原文件。
        await ReplaceWithAppendedEntriesAsync(packagePath, ct, ct,
            (OspPackage.SignatureEntryName, signature),
            (OspPackage.PublicKeyEntryName, pubKey)).ConfigureAwait(false);
    }

    /// <summary>
    /// 为一个已存在的 <c>.osp</c> 包追加 detached Ed25519 签名。Per ADR-0039 §8 / §9.
    /// 这是当前推荐的签名重载: 直接转发到 <see cref="SignEd25519Async"/>。
    /// 与 legacy RSA-SHA256 重载 <see cref="SignAsync(string, string, CancellationToken)"/> 区分,
    /// 此重载接受 32 字节 Ed25519 私钥 seed (与 <see cref="GenerateEd25519KeyPair"/> 输出一致)。
    /// </summary>
    /// <param name="packagePath">已打包的 <c>.osp</c> 文件绝对路径。</param>
    /// <param name="ed25519PrivateKey">Ed25519 私钥 (32 字节 seed, 由开发者持有)。</param>
    /// <param name="ct">取消令牌。</param>
    public Task SignAsync(string packagePath, byte[] ed25519PrivateKey, CancellationToken ct = default)
        => SignEd25519Async(packagePath, ed25519PrivateKey, ct);

    /// <summary>
    /// 为一个已存在的 <c>.osp</c> 包追加 detached Ed25519 签名 (对 manifest + 所有 DLL SHA256 摘要签名)。Per ADR-0039 §8.
    /// 使用 BouncyCastle 提供 Ed25519 支持。签名载荷哈希由 <see cref="OspPackage.ComputePayloadHashAsync"/> 计算。
    /// </summary>
    /// <param name="packagePath">已打包的 <c>.osp</c> 文件绝对路径。</param>
    /// <param name="privateKey">Ed25519 私钥 (32 字节 seed, 由开发者持有)。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task SignEd25519Async(string packagePath, byte[] privateKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (!File.Exists(packagePath))
            throw new OspPackageException($"Package not found: {packagePath}");
        if (privateKey.Length != 32)
            throw new ArgumentException("Ed25519 private key must be 32 bytes (seed).", nameof(privateKey));

        // 1) 读出 manifest + 计算载荷哈希。
        await using var pkg = await OspPackage.OpenAsync(packagePath, ct).ConfigureAwait(false);
        var manifest = await pkg.ReadManifestAsync(ct).ConfigureAwait(false);
        var payloadHash = await pkg.ComputePayloadHashAsync(manifest, ct).ConfigureAwait(false);

        // 2) 用 Ed25519 签名 (以 payloadHash 作为消息)。
        var privKeyParams = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(privateKey, 0);
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, privKeyParams);
        signer.BlockUpdate(payloadHash, 0, payloadHash.Length);
        var signature = signer.GenerateSignature();

        // 3) 导出公钥 (SPKI 格式, 与 Ed25519SignatureVerifier 的 SPKI 解析路径兼容)。
        var pubKeyParams = privKeyParams.GeneratePublicKey();
        var pubKey = pubKeyParams.GetEncoded();

        // 4) 写回 signature.sig / signature.pub 到包。
        await ReplaceWithAppendedEntriesAsync(packagePath, ct, ct,
            (OspPackage.SignatureEntryName, signature),
            (OspPackage.PublicKeyEntryName, pubKey)).ConfigureAwait(false);
    }

    /// <summary>
    /// 生成 Ed25519 密钥对。Per ADR-0039 §8.
    /// 返回 (32 字节私钥 seed, SPKI 编码公钥)。
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateEd25519KeyPair()
    {
        var random = new Org.BouncyCastle.Security.SecureRandom();
        var privKeyParams = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(random);
        var privateKey = privKeyParams.GetEncoded();
        var publicKey = privKeyParams.GeneratePublicKey().GetEncoded();
        return (privateKey, publicKey);
    }

    /// <summary>
    /// 校验一个 <c>.osp</c> 包的签名 (委托给 <see cref="ISignatureVerifier"/>)。
    /// 若 packager 构造时未注入 verifier, 返回 <see cref="SignatureResult.Untrusted"/>。
    /// </summary>
    public async Task<SignatureResult> VerifyAsync(string packagePath, bool sourceIsTrusted, CancellationToken ct = default)
    {
        if (_signatureVerifier is null) return SignatureResult.Untrusted;
        await using var pkg = await OspPackage.OpenAsync(packagePath, ct).ConfigureAwait(false);
        var manifest = await pkg.ReadManifestAsync(ct).ConfigureAwait(false);
        var (sig, pub) = pkg.ReadSignature();
        var payloadHash = await pkg.ComputePayloadHashAsync(manifest, ct).ConfigureAwait(false);
        return await _signatureVerifier.VerifyAsync(manifest, payloadHash, pub, sig, sourceIsTrusted, ct).ConfigureAwait(false);
    }

    /// <summary>重建一个 .osp 包, 在末尾追加若干 entry。用于签名写回。</summary>
    private static async Task ReplaceWithAppendedEntriesAsync(
        string packagePath,
        CancellationToken readCt,
        CancellationToken writeCt,
        params (string Name, byte[] Content)[] appended)
    {
        var tmpPath = packagePath + ".tmp";
        try
        {
            // 1) 读出原包所有 entries 内容 (内存缓冲, 包大小有 50MB 上限, 可接受)。
            Dictionary<string, byte[]> entries;
            await using (var srcPkg = await OspPackage.OpenAsync(packagePath, readCt).ConfigureAwait(false))
            {
                entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var e in srcPkg.GetArchive().Entries)
                {
                    using var es = e.Open();
                    using var ms = new MemoryStream();
                    await es.CopyToAsync(ms, readCt).ConfigureAwait(false);
                    entries[e.FullName] = ms.ToArray();
                }
            }

            // 2) 写新包: 原有 entries + 追加。
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (name, content) in entries)
                {
                    var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                    await using var es = entry.Open();
                    await es.WriteAsync(content, writeCt).ConfigureAwait(false);
                }
                foreach (var (name, content) in appended)
                {
                    var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                    await using var es = entry.Open();
                    await es.WriteAsync(content, writeCt).ConfigureAwait(false);
                }
            }

            // 3) 原子替换。
            File.Delete(packagePath);
            File.Move(tmpPath, packagePath);
        }
        finally
        {
            if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { /* best-effort */ }
        }
    }
}
