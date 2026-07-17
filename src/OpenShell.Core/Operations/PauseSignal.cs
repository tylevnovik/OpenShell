namespace OpenShell.Operations;

/// <summary>
/// 异步暂停信号。Per ADR-0044 §8.
/// <para>
/// 基于 <see cref="TaskCompletionSource{TResult}"/> 实现: PauseAsync 创建一个未完成的 TCS,
/// 操作循环通过 <see cref="WaitAsync"/> 阻塞等待; ResumeAsync 完成当前 TCS 释放阻塞。
/// 默认初始状态为 "非暂停" (已释放), 操作循环可直接通过。
/// </para>
/// <para>
/// 线程安全: 所有方法均通过锁保护内部 TCS 引用。Pause/Resume 可重入。
/// </para>
/// </summary>
internal sealed class PauseSignal
{
    private readonly object _lock = new();
    private TaskCompletionSource<bool> _tcs = NewCompletedTcs();

    /// <summary>当前是否处于暂停状态 (操作循环应阻塞等待)。</summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock) return !_tcs.Task.IsCompleted;
        }
    }

    /// <summary>设为暂停: 后续 WaitAsync 将阻塞直到 Resume。</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (!_tcs.Task.IsCompleted) return;   // 已暂停, 幂等
            _tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>恢复: 释放当前阻塞的 WaitAsync。</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_tcs.Task.IsCompleted) return;     // 未暂停, 幂等
            _tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// 等待直到非暂停或取消。Per ADR-0044 §8: 操作循环在每个检查点调用此方法。
    /// 若未暂停 (TCS 已完成) 立即返回; 否则阻塞直到 Resume 或 ct 取消。
    /// </summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool> snapshot;
        lock (_lock) snapshot = _tcs;

        if (snapshot.Task.IsCompleted) return;

        // ct 取消时把 TCS 也置为取消 (避免悬挂), 但不抛出给其他等待者。
        await using var reg = ct.Register(() =>
        {
            lock (_lock) snapshot.TrySetCanceled(ct);
        });

        try
        {
            await snapshot.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消是预期路径, 不向上传播为暂停异常; 由调用方检查 ct.ThrowIfCancellationRequested。
        }
    }

    private static TaskCompletionSource<bool> NewCompletedTcs()
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult(true);
        return tcs;
    }
}
