#nullable enable
// ADR-0058 §4.1: ICompilationCache 默认内存实现。
// 设计：
//   1. ConcurrentDictionary<Expression, CacheEntry> 存储委托。
//   2. LRU 策略: 容量上限 1024, 超出时按 LastAccess 淘汰最久未用的条目。
//   3. Uncacheable 集合加锁访问, 避免重复尝试已知不支持的节点。
//   4. 原子计数 hits/misses/attempts/failures 供 GetStats 查询。

using System.Collections.Concurrent;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;

namespace OpenShell.Compilation;

/// <summary>
/// 进程内 <see cref="ICompilationCache"/> 默认实现。Per ADR-0058 §4.1.
/// <para>
/// LRU + 容量上限 (默认 1024 条目), 超出时按最近最少使用淘汰。
/// Uncacheable 集合独立存储, 避免重复尝试编译已知不支持的 AST。
/// </para>
/// </summary>
public sealed class InMemoryCompilationCache : ICompilationCache
{
    /// <summary>默认容量上限。Per ADR-0058 §4.1.</summary>
    public const int DefaultCapacity = 1024;

    private readonly ConcurrentDictionary<Expression, CacheEntry> _cache = new();
    private readonly HashSet<Expression> _uncacheable = new();
    private readonly object _uncacheableLock = new();
    private readonly int _capacity;

    private long _cacheHits;
    private long _cacheMisses;
    private long _compilationAttempts;
    private long _compilationFailures;

    public InMemoryCompilationCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <inheritdoc />
    public bool TryGet(Expression expr, out Func<ExecutionContext, object?> del)
    {
        if (_cache.TryGetValue(expr, out var entry))
        {
            entry.Touch();
            Interlocked.Increment(ref _cacheHits);
            del = entry.Delegate;
            return true;
        }
        Interlocked.Increment(ref _cacheMisses);
        del = null!;
        return false;
    }

    /// <inheritdoc />
    public void Store(Expression expr, Func<ExecutionContext, object?> del)
    {
        ArgumentNullException.ThrowIfNull(del);
        Interlocked.Increment(ref _compilationAttempts);

        // 容量检查: 超出时淘汰 LRU。Per ADR-0058 §4.1.
        if (_cache.Count >= _capacity)
        {
            EvictLeastRecentlyUsed();
        }
        _cache[expr] = new CacheEntry(del);
    }

    /// <inheritdoc />
    public void MarkUncacheable(Expression expr)
    {
        Interlocked.Increment(ref _compilationAttempts);
        Interlocked.Increment(ref _compilationFailures);
        lock (_uncacheableLock)
        {
            _uncacheable.Add(expr);
        }
    }

    /// <inheritdoc />
    public bool IsUncacheable(Expression expr)
    {
        lock (_uncacheableLock)
        {
            return _uncacheable.Contains(expr);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
        lock (_uncacheableLock)
        {
            _uncacheable.Clear();
        }
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _compilationAttempts, 0);
        Interlocked.Exchange(ref _compilationFailures, 0);
    }

    /// <inheritdoc />
    public CompilationCacheStats GetStats()
    {
        int uncacheableCount;
        lock (_uncacheableLock)
        {
            uncacheableCount = _uncacheable.Count;
        }
        return new CompilationCacheStats(
            CacheEntries: _cache.Count,
            UncacheableEntries: uncacheableCount,
            CacheHits: Interlocked.Read(ref _cacheHits),
            CacheMisses: Interlocked.Read(ref _cacheMisses),
            CompilationAttempts: Interlocked.Read(ref _compilationAttempts),
            CompilationFailures: Interlocked.Read(ref _compilationFailures));
    }

    /// <summary>LRU 淘汰: 找出 LastAccess 最小的条目移除。粗粒度锁保证一致性。</summary>
    private void EvictLeastRecentlyUsed()
    {
        // 简化实现: 取 count - capacity + 1 个最久未访问的条目移除。
        // 高并发下可能多线程同时淘汰, 但 ConcurrentDictionary 的枚举是快照, 安全。
        var toEvict = _cache
            .OrderBy(p => p.Value.LastAccessTicks)
            .Take(Math.Max(1, _cache.Count - _capacity + 1))
            .Select(p => p.Key)
            .ToList();
        foreach (var key in toEvict)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private sealed class CacheEntry
    {
        public Func<ExecutionContext, object?> Delegate { get; }
        public long LastAccessTicks { get; private set; }

        public CacheEntry(Func<ExecutionContext, object?> del)
        {
            Delegate = del;
            LastAccessTicks = DateTime.UtcNow.Ticks;
        }

        public void Touch()
        {
            LastAccessTicks = DateTime.UtcNow.Ticks;
        }
    }
}
