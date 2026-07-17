using System.IO;
using Microsoft.Extensions.Logging;
using OpenShell;

namespace OpenShell.Sessions;

/// <summary>
/// 会话跨机器同步服务。Per ADR-0034 §9.
/// 通过 <see cref="ISessionSyncProvider"/> 上传 / 下载会话 JSON 文件。
/// 同步内容: 会话 JSON。不同步: 操作日志、命令历史 (隐私敏感)。
/// </summary>
/// <remarks>
/// 冲突解决: 最后写入胜出 (last-write-wins)。PullAsync 覆盖本地, PushAsync 覆盖远程。
/// 同步失败不阻塞本地会话操作: 所有方法捕获 <see cref="SessionSyncException"/> 并记录日志后重新抛出,
/// 由调用方决定是否提示用户 (Per ADR-0034 §9 约束: 同步冲突时必须提示用户, 不静默覆盖)。
/// </remarks>
public sealed class SessionSyncService
{
    private readonly ISessionSyncProvider _provider;
    private readonly string _baseDir;
    private readonly ILogger<SessionSyncService>? _logger;

    /// <summary>会话文件所在子目录名 (相对 _baseDir)。</summary>
    private const string SessionsSubDir = "sessions";

    public SessionSyncService(
        ISessionSyncProvider provider,
        string? baseDir = null,
        ILogger<SessionSyncService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _baseDir = baseDir ?? OpenShellPaths.Root;
        _logger = logger;
    }

    /// <summary>
    /// 推送 (上传) 指定会话到远程。Per ADR-0034 §9.
    /// 读取本地 <c>sessions/&lt;name&gt;.json</c> 并上传; 本地不存在时抛异常。
    /// </summary>
    /// <param name="sessionName">会话名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="FileNotFoundException">本地会话文件不存在。</exception>
    public async Task PushAsync(string sessionName, CancellationToken ct = default)
    {
        var localPath = GetLocalSessionPath(sessionName);
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException(
                $"Local session file not found: '{localPath}'. Call SaveAsync first.", localPath);
        }

        await using var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        try
        {
            await _provider.UploadAsync(sessionName, stream, ct).ConfigureAwait(false);
            _logger?.LogInformation("Session '{Name}' pushed to remote sync.", sessionName);
        }
        catch (SessionSyncException ex)
        {
            _logger?.LogWarning(ex, "Failed to push session '{Name}' to remote.", sessionName);
            throw;
        }
    }

    /// <summary>
    /// 拉取 (下载) 指定会话从远程, 覆盖本地。Per ADR-0034 §9.
    /// 远程不存在时返回 false (不修改本地文件)。
    /// </summary>
    /// <param name="sessionName">会话名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 下载并覆盖本地成功; false = 远程不存在。</returns>
    public async Task<bool> PullAsync(string sessionName, CancellationToken ct = default)
    {
        Stream? remote;
        try
        {
            remote = await _provider.DownloadAsync(sessionName, ct).ConfigureAwait(false);
        }
        catch (SessionSyncException ex)
        {
            _logger?.LogWarning(ex, "Failed to pull session '{Name}' from remote.", sessionName);
            throw;
        }

        if (remote is null)
        {
            _logger?.LogDebug("Remote session '{Name}' does not exist; pull skipped.", sessionName);
            return false;
        }

        await using (remote)
        {
            var localPath = GetLocalSessionPath(sessionName);
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 写入临时文件后原子替换, 避免下载中途崩溃损坏本地会话。
            var tempPath = localPath + ".sync.tmp";
            await using (var fs = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await remote.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (OperatingSystem.IsWindows() && File.Exists(localPath))
            {
                File.Replace(tempPath, localPath, destinationBackupFileName: null);
            }
            else
            {
                if (File.Exists(localPath)) File.Delete(localPath);
                File.Move(tempPath, localPath);
            }

            _logger?.LogInformation("Session '{Name}' pulled from remote sync.", sessionName);
            return true;
        }
    }

    /// <summary>检查远程是否存在指定会话。</summary>
    public async Task<bool> RemoteExistsAsync(string sessionName, CancellationToken ct = default)
    {
        try
        {
            return await _provider.ExistsAsync(sessionName, ct).ConfigureAwait(false);
        }
        catch (SessionSyncException ex)
        {
            _logger?.LogWarning(ex, "Failed to check remote existence of session '{Name}'.", sessionName);
            throw;
        }
    }

    private string GetLocalSessionPath(string sessionName) =>
        Path.Combine(_baseDir, SessionsSubDir, sessionName + ".json");
}
