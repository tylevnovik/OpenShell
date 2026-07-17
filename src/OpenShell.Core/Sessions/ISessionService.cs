namespace OpenShell.Sessions;

/// <summary>
/// 会话服务抽象。Per ADR-0034 §3, §4, §8, §13.
/// 负责：加载/创建会话、保存、崩溃检测、锁管理、快照、清除。
/// </summary>
public interface ISessionService
{
    /// <summary>当前活动会话。LoadOrCreateAsync 之前为 null。</summary>
    Session? Current { get; }

    /// <summary>按名称加载会话，不存在则创建默认会话。Per ADR-0034 §3 / §6.</summary>
    Task<Session> LoadOrCreateAsync(string sessionName, CancellationToken ct = default);

    /// <summary>持久化当前会话状态到 sessions/&lt;name&gt;.json。Per ADR-0034 §3 / §5.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// 替换当前会话状态 (如 GUI tabs 变更 / 导航历史更新)。Per ADR-0034 §11.
    /// 不立即持久化; 需配合 <see cref="SaveAsync"/> 落盘。
    /// 用于 host 在运行时更新会话的 Tabs / CurrentLocation / NavigationHistory 等字段。
    /// </summary>
    /// <param name="updated">更新后的会话实例 (通常由 <c>session with { ... }</c> 构造)。</param>
    void UpdateCurrent(Session updated);

    /// <summary>检测会话崩溃状态：lock 存在 + 持有进程是否存活。Per ADR-0034 §4 / §10.</summary>
    Task<CrashDetectionResult> DetectCrashAsync(string sessionName, CancellationToken ct = default);

    /// <summary>获取会话锁 (写入 .lock 文件含 pid/machine)。Per ADR-0034 §4 / §10.</summary>
    Task AcquireLockAsync(string sessionName, CancellationToken ct = default);

    /// <summary>释放会话锁 (删除 .lock 文件)。Per ADR-0034 §10.</summary>
    Task ReleaseLockAsync(string sessionName, CancellationToken ct = default);

    /// <summary>保存当前会话状态为命名快照。Per ADR-0034 §8.</summary>
    Task SaveSnapshotAsync(string snapshotName, CancellationToken ct = default);

    /// <summary>加载命名快照 (不存在返回 null)。Per ADR-0034 §8.</summary>
    Task<Session?> LoadSnapshotAsync(string snapshotName, CancellationToken ct = default);

    /// <summary>清除指定会话的所有持久化数据 (session + lock + 相关快照不清)。Per ADR-0034 §13.</summary>
    Task ClearSessionAsync(string sessionName, CancellationToken ct = default);
}

/// <summary>
/// 崩溃检测结果。Per ADR-0034 §4.
/// </summary>
/// <param name="LockExists">.lock 文件是否存在。</param>
/// <param name="IsProcessAlive">lock 中记录的 PID 是否存活 (LockExists=false 时为 false)。</param>
/// <param name="Pid">lock 中记录的 PID (LockExists=false 时为 null)。</param>
/// <param name="MachineName">lock 中记录的机器名 (LockExists=false 时为 null)。</param>
public sealed record CrashDetectionResult(bool LockExists, bool IsProcessAlive, int? Pid, string? MachineName);
