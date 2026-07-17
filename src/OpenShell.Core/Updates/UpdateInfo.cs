namespace OpenShell.Updates;

/// <summary>
/// 更新信息记录。Per ADR-0037 §1.
/// 由 <see cref="IUpdateService.CheckForUpdatesAsync"/> 返回，描述一个待下载的版本。
/// </summary>
public sealed record UpdateInfo(
    Version Version,
    string ReleaseNotes,
    Uri DownloadUrl,
    string Sha256,
    long SizeBytes,
    DateTimeOffset PublishedAt,
    bool IsPrerelease)
{
    /// <summary>
    /// 可选的增量补丁信息。Per ADR-0037 §8 (M5+).
    /// 当注册源为当前 → 目标版本提供二进制补丁时填充, <c>DownloadAsync</c> 优先尝试应用补丁;
    /// 失败时回退到全量下载。null 表示无补丁或主机版本不匹配。
    /// </summary>
    public PatchInfo? Patch { get; init; }
}

/// <summary>
/// 增量补丁元信息。Per ADR-0037 §8.
/// 描述一个针对特定 fromVersion → toVersion 的二进制补丁资产。
/// 补丁文件格式由 <see cref="BinaryPatcher"/> 定义 (简化 length-prefixed diff)。
/// </summary>
public sealed record PatchInfo
{
    /// <summary>补丁资产下载 URL (可与 <see cref="UpdateInfo.DownloadUrl"/> 不同)。</summary>
    public required Uri PatchUrl { get; init; }

    /// <summary>补丁适用的源版本 (必须与当前主机版本精确匹配)。</summary>
    public required string PatchFromVersion { get; init; }

    /// <summary>补丁文件的 SHA256 (hex, 小写)。空字符串表示不校验。</summary>
    public string PatchHash { get; init; } = string.Empty;

    /// <summary>补丁文件大小 (字节); 0 表示未知, 进度条按 StreamContent 长度回退。</summary>
    public long PatchSizeBytes { get; init; }
}
