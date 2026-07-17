namespace OpenShell.Operations;

/// <summary>
/// 一次操作的生命周期句柄。Per ADR-0044 §1.
/// 任务生命周期：Pending → Running → Paused → Completed / Failed / Cancelled 全状态可观测。
/// 必须 Dispose（实现 IAsyncDisposable），否则 CancellationTokenSource 与事件订阅泄漏。
/// </summary>
public interface ITaskHandle : IAsyncDisposable
{
    /// <summary>任务唯一标识（Guid.NewGuid()，不允许复用）。Per ADR-0014 §3.</summary>
    Guid TaskId { get; }

    /// <summary>操作类型名（"copy", "move", "delete", ...）。</summary>
    string Operation { get; }

    /// <summary>展示标签（"Copying 3 items to fs::D:/Backup"）。</summary>
    string DisplayLabel { get; }

    /// <summary>当前任务状态。</summary>
    TaskState State { get; }

    /// <summary>最近一次进度快照。null 表示尚未推进。</summary>
    OperationProgress? LastProgress { get; }

    /// <summary>失败时的异常。仅 State == Failed 时有值。</summary>
    Exception? Exception { get; }

    /// <summary>任务开始时间。</summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>任务完成时间。null 表示未完成。</summary>
    DateTimeOffset? CompletedAt { get; }

    /// <summary>取消令牌。命令内部所有 await ct 检查点响应此 token。</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// 是否支持暂停 (仅 Copy / Move 支持, 其他操作调用 PauseAsync 抛 NotSupportedException)。
    /// Per ADR-0044 §8.
    /// </summary>
    bool SupportsPause { get; }

    /// <summary>进度变化事件。采样频率 ≤ 20 Hz（ITaskHandle 内部统一节流，ViewModel 端不再节流）。</summary>
    event EventHandler<OperationProgress>? ProgressChanged;

    /// <summary>状态变化事件。</summary>
    event EventHandler<TaskState>? StateChanged;

    /// <summary>请求取消。返回后任务最终进入 Cancelled 状态。可重入，不抛异常。</summary>
    Task CancelAsync();

    /// <summary>
    /// 暂停任务 (如果操作支持, 如 Copy / Move; 不支持时抛 NotSupportedException)。Per ADR-0044 §8.
    /// 暂停后任务进入 Paused 状态, 操作循环在下一个检查点阻塞等待 ResumeAsync。
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// 恢复已暂停的任务。Per ADR-0044 §8.
    /// 任务从 Paused 回到 Running, 操作循环继续执行。
    /// </summary>
    Task ResumeAsync();
}

/// <summary>
/// 任务中心。Per ADR-0044 §1.
/// 维护当前活动 + 最近完成任务列表（默认 50 条），不持久化（重启后清空）。
/// 操作引擎在创建任务时调用 Register，ViewModel 订阅 TaskAdded / TaskRemoved 更新 UI。
/// </summary>
public interface ITaskCenter
{
    /// <summary>当前活动任务列表（Pending / Running / Paused）。</summary>
    IReadOnlyList<ITaskHandle> ActiveTasks { get; }

    /// <summary>最近完成任务列表（Completed / Failed / Cancelled），默认 50 条 FIFO 丢弃。</summary>
    IReadOnlyList<ITaskHandle> RecentCompleted { get; }

    /// <summary>任务加入活动列表时触发。</summary>
    event EventHandler<ITaskHandle>? TaskAdded;

    /// <summary>任务离开活动列表（完成 / 取消 / 失败）时触发。</summary>
    event EventHandler<ITaskHandle>? TaskRemoved;

    /// <summary>注册新任务，返回句柄。操作引擎内部调用。</summary>
    ITaskHandle Register(TaskRegistration registration);

    /// <summary>按 ID 查找活动或已完成任务。</summary>
    ITaskHandle? Find(Guid taskId);
}

/// <summary>任务状态机。Per ADR-0044 §1.</summary>
public enum TaskState { Pending, Running, Paused, Completed, Failed, Cancelled }

/// <summary>
/// 任务注册参数。Per ADR-0044 §1.
/// 不可变 record，由操作引擎在 BeginXxx 时构造。
/// </summary>
public sealed record TaskRegistration
{
    /// <summary>操作类型名（"copy", "move", "delete", ...）。必填。</summary>
    public required string Operation { get; init; }

    /// <summary>展示标签。必填。</summary>
    public required string DisplayLabel { get; init; }

    /// <summary>取消令牌源。必填。任务结束时由 ITaskHandle.Dispose 释放。</summary>
    public required CancellationTokenSource Cts { get; init; }

    /// <summary>是否支持暂停（仅 Copy / Move 支持，其他操作调用 PauseAsync 抛 NotSupportedException）。</summary>
    public bool SupportsPause { get; init; } = false;

    /// <summary>是否默认后台运行（不弹模态进度对话框）。</summary>
    public bool RunInBackgroundByDefault { get; init; } = false;

    /// <summary>
    /// 目标路径 (用于 OperationCompletedEvent 通知)。Per ADR-0044 §7.
    /// 可空: 部分操作 (如 touch) 无明确目标。
    /// </summary>
    public string? TargetPath { get; init; }
}
