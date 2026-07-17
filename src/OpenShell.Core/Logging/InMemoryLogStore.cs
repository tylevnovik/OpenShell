using Microsoft.Extensions.Logging;

namespace OpenShell.Logging;

/// <summary>
/// 基于 <see cref="ILogStore"/> 的内存环形缓冲区默认实现。Per ADR-0031 §2.
/// 线程安全 (lock); 容量上限到达时自动丢弃最旧条目 (FIFO)。
/// </summary>
public sealed class InMemoryLogStore : ILogStore
{
    private const int DefaultCapacity = 1000;

    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly int _capacity;

    /// <summary>构造 InMemoryLogStore。</summary>
    /// <param name="capacity">环形缓冲区容量, 默认 1000 条。超出自动丢弃最旧。</param>
    public InMemoryLogStore(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) capacity = DefaultCapacity;
        _capacity = capacity;
    }

    /// <inheritdoc />
    public event EventHandler<LogEntry>? EntryAppended;

    /// <inheritdoc />
    public void Append(LogEntry entry)
    {
        EventHandler<LogEntry>? handler;
        lock (_lock)
        {
            _entries.AddLast(entry);
            // 容量超限时丢弃最旧条目 (FIFO)。
            while (_entries.Count > _capacity)
            {
                _entries.RemoveFirst();
            }
            handler = EntryAppended;
        }

        // 在锁外触发事件, 避免订阅者回调内再次访问 store 时死锁。
        handler?.Invoke(this, entry);
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> Recent(int count = 100)
    {
        if (count <= 0) return Array.Empty<LogEntry>();

        lock (_lock)
        {
            if (_entries.Count <= count)
            {
                return _entries.ToArray();
            }

            // 取最近 count 条: 跳过前面 (Count - count) 条。
            var skip = _entries.Count - count;
            var result = new LogEntry[count];
            var node = _entries.First;
            for (int i = 0; i < skip; i++)
            {
                node = node!.Next;
            }
            for (int i = 0; i < count; i++)
            {
                result[i] = node!.Value;
                node = node.Next;
            }
            return result;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> Filter(LogFilter filter)
    {
        // null filter 视为无过滤; 等价于 Recent(capacity)。
        filter ??= new LogFilter();

        lock (_lock)
        {
            var result = new List<LogEntry>();
            foreach (var entry in _entries)
            {
                if (Matches(entry, filter))
                {
                    result.Add(entry);
                }
            }
            return result;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private static bool Matches(LogEntry entry, LogFilter filter)
    {
        if (filter.MinLevel is { } minLevel && entry.Level < minLevel)
        {
            return false;
        }

        if (filter.Category is { } cat
            && !string.Equals(entry.Category, cat, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Since is { } since && entry.Timestamp < since)
        {
            return false;
        }

        if (filter.Until is { } until && entry.Timestamp > until)
        {
            return false;
        }

        if (filter.MessageContains is { } needle
            && !entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
