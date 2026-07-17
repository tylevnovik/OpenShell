using System.Collections.Concurrent;

namespace OpenShell.Operations;

/// <summary>
/// 默认 <see cref="IOperationTracker"/> 实现。Per ADR-0016 §3.
/// 使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 维护每个 provider 的计数 + 等待信号。
/// 归零时通过 <see cref="TaskCompletionSource"/> 唤醒所有 <see cref="WaitForProviderAsync"/> 等待者。
/// 线程安全; 计数不会降到负数。
/// </summary>
public sealed class OperationTracker : IOperationTracker
{
    /// <summary>
    /// 每个 provider 的 in-flight 状态: 计数 + (归零时唤醒等待者的 TCS)。
    /// TCS 在计数从 0 升到 1 时清空, 在计数从 1 降到 0 时被 SetResult。
    /// </summary>
    private sealed class CountState
    {
        public int Count;
        // 单个 TCS 足够: 同一 provider 通常只有一个卸载流程在等待。
        // 多个等待者共享同一 TCS.Task, 全部被唤醒。
        public TaskCompletionSource? ZeroTcs;
    }

    private readonly ConcurrentDictionary<string, CountState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Increment(string providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return;
        var state = _states.GetOrAdd(providerName, _ => new CountState());
        lock (state)
        {
            // 0 → 1: 取消任何 pending 的 ZeroTcs (因为现在又有人在操作了)。
            if (state.Count == 0)
            {
                state.ZeroTcs = null;
            }
            state.Count++;
        }
    }

    /// <inheritdoc />
    public void Decrement(string providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return;
        if (!_states.TryGetValue(providerName, out var state)) return;

        TaskCompletionSource? toComplete = null;
        lock (state)
        {
            if (state.Count <= 0)
            {
                state.Count = 0;
                return;
            }
            state.Count--;
            if (state.Count == 0 && state.ZeroTcs is not null)
            {
                toComplete = state.ZeroTcs;
                state.ZeroTcs = null;
            }
        }

        // 在锁外完成 TCS, 避免锁内执行 continuation (RunContinuationsAsynchronously 已防止, 但仍保险)。
        toComplete?.TrySetResult();
    }

    /// <inheritdoc />
    public int GetInFlightCount(string providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return 0;
        return _states.TryGetValue(providerName, out var s) ? s.Count : 0;
    }

    /// <inheritdoc />
    public ValueTask<bool> WaitForProviderAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(providerName)) return ValueTask.FromResult(true);

        var state = _states.GetOrAdd(providerName, _ => new CountState());
        TaskCompletionSource tcs;
        lock (state)
        {
            if (state.Count == 0) return ValueTask.FromResult(true);
            // 复用或创建等待 TCS: 多个等待者共享同一 TCS。
            tcs = state.ZeroTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return WaitCoreAsync(tcs, cancellationToken);
    }

    private static async ValueTask<bool> WaitCoreAsync(TaskCompletionSource tcs, CancellationToken ct)
    {
        // 注册取消: 取消时 TrySetCanceled 唤醒等待者。
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            await tcs.Task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
