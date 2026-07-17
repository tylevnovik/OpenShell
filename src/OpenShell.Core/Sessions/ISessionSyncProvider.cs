namespace OpenShell.Sessions;

/// <summary>
/// 会话跨机器同步传输后端抽象。Per ADR-0034 §9.
/// 实现负责将会话 JSON 文件上传 / 下载到远程存储 (WebDAV / S3 等)。
/// 同步内容: 会话 JSON / 快照。不同步: 操作日志、命令历史 (隐私敏感, Per ADR-0034 §9)。
/// </summary>
/// <remarks>
/// 远程路径约定: <c>&lt;endpoint&gt;/sessions/&lt;sessionName&gt;.json</c>。
/// 冲突解决策略: 最后写入胜出 (last-write-wins), 用户可强制拉 / 推 (Per ADR-0034 §9)。
/// </remarks>
public interface ISessionSyncProvider
{
    /// <summary>上传会话 JSON 内容到远程 <c>sessions/&lt;sessionName&gt;.json</c>。</summary>
    /// <param name="sessionName">会话名 (如 "work" / "default")。</param>
    /// <param name="content">会话 JSON 字节流。</param>
    /// <param name="ct">取消令牌。</param>
    Task UploadAsync(string sessionName, Stream content, CancellationToken ct = default);

    /// <summary>下载远程会话 JSON。不存在时返回 null。</summary>
    /// <param name="sessionName">会话名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>会话 JSON 字节流, 或 null (远程不存在)。</returns>
    Task<Stream?> DownloadAsync(string sessionName, CancellationToken ct = default);

    /// <summary>检查远程会话 JSON 是否存在。</summary>
    Task<bool> ExistsAsync(string sessionName, CancellationToken ct = default);
}

/// <summary>
/// 会话同步异常。Per ADR-0034 §9: 同步失败不阻塞本地会话操作, 由上层捕获并提示用户。
/// </summary>
public sealed class SessionSyncException : Exception
{
    public SessionSyncException(string message) : base(message) { }
    public SessionSyncException(string message, Exception innerException) : base(message, innerException) { }
}
