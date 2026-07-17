using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Paths;

namespace OpenShell.History;

/// <summary>
/// 基于 JSON Lines 文件的 <see cref="IHistoryService"/> 默认实现。Per ADR-0020, ADR-0022 §6.
/// 持久化到 <c>~/.openshell/history.jsonl</c>, 每行一条 JSON 记录, append-only。
/// 加载时读取全部内容到内存; 写入采用 debounce 策略 (默认 5s flush 一次)。
/// 默认保留 1000 条, 超出自动 FIFO 裁剪。
/// </summary>
public sealed class FileHistoryService : IHistoryService, IAsyncDisposable
{
    private const int DefaultMaxEntries = 1000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly int _maxEntries;
    private readonly object _lock = new();
    private List<HistoryEntry> _entries;
    private bool _dirty;
    private readonly Timer _flushTimer;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>构造 FileHistoryService。</summary>
    /// <param name="path">history.jsonl 文件路径, 默认 <see cref="OpenShellPaths.History"/>。</param>
    /// <param name="maxEntries">最大保留条数, 默认 1000。</param>
    public FileHistoryService(string? path = null, int maxEntries = DefaultMaxEntries)
    {
        _path = path ?? OpenShellPaths.History;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        _entries = Load();
        _dirty = false;
        _flushTimer = new Timer(
            static state => ((FileHistoryService)state!).FlushIfDirty(),
            this,
            FlushInterval,
            FlushInterval);
    }

    /// <inheritdoc />
    public IReadOnlyList<HistoryEntry> Recent
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Add(string command, bool success, int exitCode)
    {
        var entry = new HistoryEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Command = command,
            Success = success,
            ExitCode = exitCode,
            WorkingDirectory = GetCurrentWorkingDirectory(),
        };

        lock (_lock)
        {
            _entries.Add(entry);
            // FIFO 裁剪: 超出容量时丢弃最旧条目。
            while (_entries.Count > _maxEntries)
            {
                _entries.RemoveAt(0);
            }
            _dirty = true;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _dirty = true;
        }

        // 同步删除持久化文件 (不等待 debounce)。
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // 文件删除失败不阻塞内存清空, 下次 flush 会重写空文件。
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HistoryEntry> Search(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return Array.Empty<HistoryEntry>();
        }

        lock (_lock)
        {
            // 倒序返回 (最近在前), 大小写不敏感子串匹配。
            var results = new List<HistoryEntry>();
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Command.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(_entries[i]);
                }
            }
            return results;
        }
    }

    private static ItemPath GetCurrentWorkingDirectory()
    {
        try
        {
            return new ItemPath
            {
                Provider = "fs",
                InternalPath = Environment.CurrentDirectory.Replace('\\', '/'),
            };
        }
        catch
        {
            return new ItemPath { Provider = "fs", InternalPath = "/" };
        }
    }

    /// <summary>从 <c>history.jsonl</c> 加载全部记录到内存。损坏的行跳过, 不阻断加载。</summary>
    private List<HistoryEntry> Load()
    {
        var entries = new List<HistoryEntry>();
        if (!File.Exists(_path))
        {
            return entries;
        }

        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line, _jsonOptions);
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

        // 加载后裁剪到容量上限。
        while (entries.Count > _maxEntries)
        {
            entries.RemoveAt(0);
        }

        return entries;
    }

    /// <summary>若 dirty 则把内存中的全部记录重写到 history.jsonl。debounce flush 的核心。</summary>
    private void FlushIfDirty()
    {
        List<HistoryEntry> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            snapshot = _entries.ToList();
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 整体重写 (非 append), 保证裁剪后的记录数正确。
            using var writer = new StreamWriter(_path, append: false);
            foreach (var entry in snapshot)
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, _jsonOptions));
            }
        }
        catch (IOException)
        {
            // flush 失败: 重新标记 dirty, 下次 timer 再试。
            lock (_lock)
            {
                _dirty = true;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _flushTimer.DisposeAsync().ConfigureAwait(false);
        // 关闭时强制 flush 一次, 保证不丢数据。
        FlushIfDirty();
    }
}
