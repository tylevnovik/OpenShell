using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShell.Operations;

/// <summary>
/// 基于 JSON Lines 文件的 <see cref="IOperationJournal"/> 默认实现。Per ADR-0020 §5.
/// 持久化到 <c>~/.openshell/journal.jsonl</c> (JSON Lines, append-only)。
/// 容量上限 10000 条 FIFO; 启动时加载最近 1000 条到内存。
/// 写入采用 <see cref="FileStream"/> 独占写锁 (FileShare.None) 保证多窗口并发安全。
/// </summary>
public sealed class FileOperationJournal : IOperationJournal, IAsyncDisposable
{
    /// <summary>日志最大保留条数 (FIFO)。Per ADR-0020 §5.</summary>
    public const int MaxEntries = 10000;

    /// <summary>启动时加载到内存的最近条目数。Per ADR-0020 §5.</summary>
    public const int LoadOnStartupCount = 1000;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private List<OperationJournalEntry> _entries;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>新操作追加事件。订阅者 (如 IUndoService) 据此清空本地 Redo 栈。Per ADR-0020 §8.</summary>
    public event EventHandler<OperationJournalEntry>? Appended;

    /// <summary>构造 FileOperationJournal。</summary>
    /// <param name="path">journal.jsonl 文件路径, 默认 <see cref="OpenShell.OpenShellPaths.Journal"/>。</param>
    public FileOperationJournal(string? path = null)
    {
        _path = path ?? OpenShell.OpenShellPaths.Journal;
        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        _entries = LoadRecent(LoadOnStartupCount);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(OperationJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                _entries.Add(entry);
                // FIFO 裁剪: 超出容量时丢弃最旧条目。Per ADR-0020 §5.
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveAt(0);
                }
            }

            // 追加到文件 (append-only, 独占写锁)。
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // FileShare.None 保证多窗口并发安全 (ADR-0020 §10)。
            await using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true);
            await using var writer = new StreamWriter(fs);
            var line = JsonSerializer.Serialize(entry, _jsonOptions);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        // 触发事件 (在锁外, 避免订阅者回调死锁)。
        Appended?.Invoke(this, entry);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OperationJournalEntry>> ReadRecentAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        if (count <= 0) count = 100;

        lock (_lock)
        {
            if (_entries.Count <= count)
            {
                return ValueTask.FromResult<IReadOnlyList<OperationJournalEntry>>(_entries.ToList());
            }
            var skip = _entries.Count - count;
            return ValueTask.FromResult<IReadOnlyList<OperationJournalEntry>>(_entries.Skip(skip).ToList());
        }
    }

    /// <inheritdoc />
    public async ValueTask MarkUndoneAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                var idx = _entries.FindIndex(e => e.EntryId == entryId);
                if (idx < 0) return;
                _entries[idx] = _entries[idx] with { IsUndone = true };
            }
            await RewriteFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask MarkRedoneAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                var idx = _entries.FindIndex(e => e.EntryId == entryId);
                if (idx < 0) return;
                _entries[idx] = _entries[idx] with { IsUndone = false };
            }
            await RewriteFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = DateTimeOffset.UtcNow - olderThan;
            lock (_lock)
            {
                // 清理过期记录 + 已 Undo 记录 (ADR-0020 §5 + §8)。
                _entries.RemoveAll(e => e.Timestamp < cutoff || e.IsUndone);
            }
            await RewriteFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>从 <c>journal.jsonl</c> 加载最近 <paramref name="count"/> 条记录到内存。损坏的行跳过。</summary>
    private List<OperationJournalEntry> LoadRecent(int count)
    {
        var entries = new List<OperationJournalEntry>();
        if (!File.Exists(_path))
        {
            return entries;
        }

        try
        {
            // 读取全部行后取最后 count 条 (容量上限保证内存可控)。
            var lines = File.ReadAllLines(_path);
            var start = Math.Max(0, lines.Length - count);
            for (int i = start; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<OperationJournalEntry>(line, _jsonOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // 跳过损坏的行, 继续解析后续记录。
                }
            }
        }
        catch (IOException)
        {
            // 文件读取失败: 返回空列表, 不阻断启动。
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足: 返回空列表。
        }

        return entries;
    }

    /// <summary>把内存中的全部记录重写到 journal.jsonl (整体重写, 用于 MarkUndone/Purge 后状态同步)。</summary>
    private async Task RewriteFileAsync(CancellationToken cancellationToken)
    {
        List<OperationJournalEntry> snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToList();
        }

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 整体重写 (FileShare.None 独占写锁, 保证并发安全)。
        await using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 8192, useAsync: true);
        await using var writer = new StreamWriter(fs);
        foreach (var entry in snapshot)
        {
            var line = JsonSerializer.Serialize(entry, _jsonOptions);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // 关闭时强制重写一次, 保证 MarkUndone 的状态持久化。
            try { await RewriteFileAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 关闭时写入失败忽略, 不抛异常 */ }
        }
        finally
        {
            _writeLock.Release();
        }
        _writeLock.Dispose();
    }
}
