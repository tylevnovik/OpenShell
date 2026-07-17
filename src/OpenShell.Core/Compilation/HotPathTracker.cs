#nullable enable
// ADR-0058 §3: 热点路径检测器。
// 设计：
//   1. 跟踪每个 Expression 的调用次数，识别热点路径。
//   2. 达 HotPathThreshold (默认 32) 触发 Tier 1 编译。
//   3. 达 OptimizationThreshold (默认 1024) 触发 Tier 2 编译（预留）。
//   4. ConcurrentDictionary + Interlocked 原子计数，线程安全。
//   5. 滑动窗口衰减（默认 60 秒）避免冷代码占满缓存。

using System.Collections.Concurrent;
using OpenShell.Parsing.Ast;

namespace OpenShell.Compilation;

/// <summary>
/// 热点路径检测器。Per ADR-0058 §3.
/// <para>
/// 跟踪每个 AST 节点的调用次数，识别热点路径触发 JIT 编译。
/// 基于 ConcurrentDictionary + Interlocked 原子计数，线程安全。
/// </para>
/// </summary>
public sealed class HotPathTracker
{
    /// <summary>Tier 1 编译触发阈值。Per ADR-0058 §3.1.</summary>
    public const int HotPathThreshold = 32;

    /// <summary>Tier 2 优化触发阈值（预留）。Per ADR-0058 §3.1.</summary>
    public const int OptimizationThreshold = 1024;

    /// <summary>滑动窗口长度（毫秒）。Per ADR-0058 §3.1.</summary>
    public const int TrackingWindowMs = 60_000;

    private readonly ConcurrentDictionary<Expression, InvocationRecord> _records = new();
    private long _lastDecayTicks = DateTime.UtcNow.Ticks;

    /// <summary>记录一次调用。Per ADR-0058 §3.</summary>
    public void RecordInvocation(Expression expr)
    {
        MaybeDecay();
        var now = DateTime.UtcNow.Ticks;
        var record = _records.AddOrUpdate(
            expr,
            _ => new InvocationRecord { Count = 1, LastInvocationTicks = now },
            (_, existing) => new InvocationRecord
            {
                Count = existing.Count + 1,
                LastInvocationTicks = now,
            });
    }

    /// <summary>查询调用次数。</summary>
    public int GetInvocationCount(Expression expr)
        => _records.TryGetValue(expr, out var r) ? r.Count : 0;

    /// <summary>是否达到 Tier 1 编译阈值。Per ADR-0058 §3.1.</summary>
    public bool IsHotPath(Expression expr)
        => GetInvocationCount(expr) >= HotPathThreshold;

    /// <summary>是否达到 Tier 2 优化阈值（预留）。Per ADR-0058 §3.1.</summary>
    public bool ShouldOptimize(Expression expr)
        => GetInvocationCount(expr) >= OptimizationThreshold;

    /// <summary>编译完成后重置计数（避免重复编译）。Per ADR-0058 §3.</summary>
    public void Reset(Expression expr)
        => _records.TryRemove(expr, out _);

    /// <summary>
    /// 滑动窗口衰减：每 TrackingWindowMs 毫秒对所有计数 ×0.5。
    /// Per ADR-0058 §3.1: 避免长期未访问的代码占满缓存。
    /// </summary>
    private void MaybeDecay()
    {
        var now = DateTime.UtcNow.Ticks;
        var lastDecay = Interlocked.Read(ref _lastDecayTicks);
        if (now - lastDecay < TrackingWindowMs * TimeSpan.TicksPerMillisecond)
            return;

        // CAS: 只让一个线程执行衰减。
        if (Interlocked.CompareExchange(ref _lastDecayTicks, now, lastDecay) != lastDecay)
            return;

        // 衰减: count /= 2, 清除 count==0 的条目。
        var keysToRemove = new List<Expression>();
        foreach (var kvp in _records)
        {
            var newCount = kvp.Value.Count / 2;
            if (newCount <= 0)
            {
                keysToRemove.Add(kvp.Key);
            }
            else
            {
                _records[kvp.Key] = new InvocationRecord
                {
                    Count = newCount,
                    LastInvocationTicks = kvp.Value.LastInvocationTicks,
                };
            }
        }
        foreach (var key in keysToRemove)
        {
            _records.TryRemove(key, out _);
        }
    }

    private record struct InvocationRecord
    {
        public int Count;
        public long LastInvocationTicks;
    }
}
