using System.Collections.Concurrent;
using OpenShell.Events;

namespace OpenShell.Operations;

/// <summary>
/// 默认 <see cref="ITaskCenter"/> 内存实现。Per ADR-0044 §1 + §12.
/// 线程安全；活动与最近完成任务列表均不持久化（重启清空）。
/// 如需历史用 Get-OperationLog 命令查 ADR-0022 journal.jsonl。
/// </summary>
public sealed class InMemoryTaskCenter : ITaskCenter
{
    /// <summary>最近完成任务列表的默认容量。Per ADR-0044 §1（默认 50 条 FIFO 丢弃）。</summary>
    public const int RecentCompletedCapacity = 50;

    private readonly ConcurrentDictionary<Guid, TaskHandle> _active = new();
    private readonly object _recentLock = new();
    private readonly IEventBus? _eventBus;
    private EventHandler<ITaskHandle>? _taskAdded;
    private EventHandler<ITaskHandle>? _taskRemoved;

    /// <summary>
    /// 构造 InMemoryTaskCenter。
    /// </summary>
    /// <param name="eventBus">
    /// 可选事件总线。注入后, 任务进入终态时发布 <see cref="OperationCompletedEvent"/> (Per ADR-0044 §7)。
    /// </param>
    public InMemoryTaskCenter(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    /// <inheritdoc />
    public IReadOnlyList<ITaskHandle> ActiveTasks =>
        _active.Values.OrderBy(h => h.StartedAt).ToList<ITaskHandle>();

    /// <inheritdoc />
    public IReadOnlyList<ITaskHandle> RecentCompleted
    {
        get
        {
            lock (_recentLock) return _recentCompleted.Reverse().ToList<ITaskHandle>();
        }
    }

    private readonly ConcurrentQueue<TaskHandle> _recentCompleted = new();

    /// <inheritdoc />
    public event EventHandler<ITaskHandle>? TaskAdded
    {
        add { _taskAdded += value; }
        remove { _taskAdded -= value; }
    }

    /// <inheritdoc />
    public event EventHandler<ITaskHandle>? TaskRemoved
    {
        add { _taskRemoved += value; }
        remove { _taskRemoved -= value; }
    }

    /// <inheritdoc />
    public ITaskHandle Register(TaskRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var handle = new TaskHandle(registration, this, _eventBus);
        _active[handle.TaskId] = handle;
        _taskAdded?.Invoke(this, handle);
        return handle;
    }

    /// <inheritdoc />
    public ITaskHandle? Find(Guid taskId) =>
        _active.TryGetValue(taskId, out var active) ? active
        : (RecentCompleted.FirstOrDefault(h => h.TaskId == taskId) as ITaskHandle);

    /// <summary>由 TaskHandle 在终态时调用，把任务从活动列表移到最近完成。</summary>
    internal void OnCompleted(TaskHandle handle)
    {
        if (!_active.TryRemove(handle.TaskId, out _)) return;
        _taskRemoved?.Invoke(this, handle);

        lock (_recentLock)
        {
            _recentCompleted.Enqueue(handle);
            while (_recentCompleted.Count > RecentCompletedCapacity && _recentCompleted.TryDequeue(out _)) { }
        }
    }
}

/// <summary>
/// 默认 <see cref="ITaskHandle"/> 实现。Per ADR-0044 §1 + §10.
/// 进度事件在内部统一节流（≤ 20 Hz / 50ms 一次），ViewModel 端不再节流。
/// 支持 Pause/Resume (Per ADR-0044 §8): 仅当 <see cref="TaskRegistration.SupportsPause"/> 为 true 时可用。
/// </summary>
internal sealed class TaskHandle : ITaskHandle
{
    /// <summary>进度事件采样节流阈值（毫秒）。Per ADR-0044 §10. ≤ 20 Hz.</summary>
    private const int ProgressEmitThrottleMs = 50;

    private readonly TaskRegistration _registration;
    private readonly InMemoryTaskCenter _center;
    private readonly IEventBus? _eventBus;
    private readonly PauseSignal _pauseSignal = new();
    private readonly object _lock = new();
    private TaskState _state = TaskState.Pending;
    private OperationProgress? _lastProgress;
    private Exception? _exception;
    private DateTimeOffset? _completedAt;
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
    private EventHandler<OperationProgress>? _progressChanged;
    private EventHandler<TaskState>? _stateChanged;
    private int _disposed;

    public TaskHandle(TaskRegistration registration, InMemoryTaskCenter center, IEventBus? eventBus)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _eventBus = eventBus;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public Guid TaskId { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public string Operation => _registration.Operation;

    /// <inheritdoc />
    public string DisplayLabel => _registration.DisplayLabel;

    /// <inheritdoc />
    public TaskState State
    {
        get { lock (_lock) return _state; }
    }

    /// <inheritdoc />
    public OperationProgress? LastProgress
    {
        get { lock (_lock) return _lastProgress; }
    }

    /// <inheritdoc />
    public Exception? Exception
    {
        get { lock (_lock) return _exception; }
    }

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset? CompletedAt
    {
        get { lock (_lock) return _completedAt; }
    }

    /// <inheritdoc />
    public CancellationToken CancellationToken => _registration.Cts.Token;

    /// <inheritdoc />
    public bool SupportsPause => _registration.SupportsPause;

    /// <summary>
    /// 内部暂停信号 (供 OperationEngine 操作循环 await)。Per ADR-0044 §8.
    /// 仅当 <see cref="SupportsPause"/> 为 true 时操作循环才使用此信号。
    /// </summary>
    internal PauseSignal PauseSignal => _pauseSignal;

    /// <inheritdoc />
    public event EventHandler<OperationProgress>? ProgressChanged
    {
        add { lock (_lock) _progressChanged += value; }
        remove { lock (_lock) _progressChanged -= value; }
    }

    /// <inheritdoc />
    public event EventHandler<TaskState>? StateChanged
    {
        add { lock (_lock) _stateChanged += value; }
        remove { lock (_lock) _stateChanged -= value; }
    }

    /// <summary>
    /// 由操作引擎调用来推进进度。内部节流：50ms 内的更新只刷新 LastProgress 字段不触发事件。
    /// 完成时（IsCompleted = true）强制推送一次最终值。Per ADR-0044 §10.
    /// </summary>
    public void ReportProgress(OperationProgress progress)
    {
        EventHandler<OperationProgress>? handlers;
        bool shouldEmit;
        lock (_lock)
        {
            _lastProgress = progress;
            var now = DateTimeOffset.UtcNow;
            shouldEmit = progress.IsCompleted
                || (now - _lastEmit).TotalMilliseconds >= ProgressEmitThrottleMs;
            if (shouldEmit) _lastEmit = now;
            handlers = _progressChanged;
        }
        if (shouldEmit) handlers?.Invoke(this, progress);
    }

    /// <summary>由操作引擎调用，标记任务进入 Running 状态。</summary>
    public void MarkRunning()
    {
        TransitionTo(TaskState.Running);
    }

    /// <summary>由操作引擎调用，标记任务成功完成。</summary>
    public void MarkCompleted()
    {
        TransitionTo(TaskState.Completed);
    }

    /// <summary>由操作引擎调用，标记任务失败。</summary>
    public void MarkFailed(Exception ex)
    {
        lock (_lock) _exception = ex;
        TransitionTo(TaskState.Failed);
    }

    /// <summary>由操作引擎调用，标记任务被取消。</summary>
    public void MarkCancelled()
    {
        TransitionTo(TaskState.Cancelled);
    }

    /// <inheritdoc />
    public Task CancelAsync()
    {
        try { _registration.Cts.Cancel(); } catch { /* 取消必须可重入，不抛异常 */ }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        if (!_registration.SupportsPause)
        {
            throw new NotSupportedException(
                $"Operation '{_registration.Operation}' does not support pause. Per ADR-0044 §8.");
        }
        _pauseSignal.Pause();
        TransitionTo(TaskState.Paused);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAsync()
    {
        if (!_registration.SupportsPause)
        {
            throw new NotSupportedException(
                $"Operation '{_registration.Operation}' does not support pause. Per ADR-0044 §8.");
        }
        _pauseSignal.Resume();
        // 仅当处于 Paused 状态时回到 Running; 其他状态 (如已完成) 忽略。
        // 不在此处持锁调 TransitionTo (避免重入), 先读取状态再转换。
        bool shouldResume;
        lock (_lock) shouldResume = _state == TaskState.Paused;
        if (shouldResume)
        {
            TransitionTo(TaskState.Running);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        try { _registration.Cts.Dispose(); } catch { /* 防御性 */ }
        return ValueTask.CompletedTask;
    }

    private void TransitionTo(TaskState newState)
    {
        EventHandler<TaskState>? handlers;
        lock (_lock)
        {
            if (_state == newState) return;
            // 终态不可逆。
            if (_state is TaskState.Completed or TaskState.Failed or TaskState.Cancelled) return;
            _state = newState;
            if (newState is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
            {
                _completedAt = DateTimeOffset.UtcNow;
            }
            handlers = _stateChanged;
        }
        handlers?.Invoke(this, newState);

        // 终态后通知 TaskCenter 把任务从活动列表移到最近完成, 并发布 OperationCompletedEvent。Per ADR-0044 §7.
        if (newState is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
        {
            PublishCompletedEvent(newState);
            _center.OnCompleted(this);
        }
    }

    /// <summary>发布 OperationCompletedEvent (Per ADR-0044 §7)。成功/失败/取消均发布, 携带耗时与字节数。</summary>
    private void PublishCompletedEvent(TaskState terminalState)
    {
        if (_eventBus is null) return;

        bool success = terminalState == TaskState.Completed;
        long bytes = LastProgress?.Completed ?? 0;
        TimeSpan? duration = CompletedAt - StartedAt;

        try
        {
            _eventBus.Publish(new OperationCompletedEvent
            {
                TaskId = TaskId,
                Operation = Operation,
                Success = success,
                Exception = success ? null : Exception,
                TargetPath = _registration.TargetPath,
                Duration = duration,
                BytesProcessed = bytes,
            });
        }
        catch
        {
            // 事件发布失败不应影响任务终态流转。Per ADR-0040 §3: 单个订阅者异常不影响其他订阅者。
        }
    }
}
