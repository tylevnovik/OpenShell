using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShell.Recent;

/// <summary>
/// 基于 JSON Lines 文件的 <see cref="IRecentService"/> 默认实现。Per ADR-0028 §7.
/// <para>
/// 持久化到 <c>~/.openshell/recent.jsonl</c>, 每行一条 JSON 记录。
/// 由于需要重排顺序并裁剪, 写入策略为整体重写 (非 append)。
/// 默认保留 20 条; 文件缺失或行损坏时静默跳过 (不抛异常)。
/// </para>
/// </summary>
public sealed class FileRecentService : IRecentService
{
    private const int DefaultMaxEntries = 20;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly int _maxEntries;
    private readonly object _lock = new();
    private List<RecentEntry> _entries;

    /// <summary>
    /// 构造 FileRecentService。
    /// </summary>
    /// <param name="filePath">recent.jsonl 文件路径; 默认 <see cref="OpenShellPaths.RecentFile"/>; 测试可注入。</param>
    /// <param name="maxEntries">最大保留条数 (默认 20, per ADR-0028 §7); 非正数回退到默认值。</param>
    public FileRecentService(string? filePath = null, int maxEntries = DefaultMaxEntries)
    {
        _filePath = filePath ?? OpenShellPaths.RecentFile;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        _entries = Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<RecentEntry> Recent
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
    public void RecordAccess(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        var entry = new RecentEntry(path, DateTimeOffset.UtcNow);

        lock (_lock)
        {
            // 已存在则更新时间戳并移到顶部; 否则前插。
            var idx = _entries.FindIndex(
                e => string.Equals(e.Path, path, StringComparison.Ordinal));
            if (idx >= 0)
            {
                _entries.RemoveAt(idx);
            }
            _entries.Insert(0, entry);

            // 超出容量: 从尾部丢弃最旧条目。
            while (_entries.Count > _maxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }

            Persist();
        }

        OnRecentChanged();
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();

            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            catch (IOException)
            {
                // best-effort: 删除失败不阻塞内存清空, 下次 persist 会重写空文件。
            }
        }

        OnRecentChanged();
    }

    /// <inheritdoc />
    public void Reload()
    {
        lock (_lock)
        {
            _entries = Load();
        }
        OnRecentChanged();
    }

    /// <inheritdoc />
    public event EventHandler? RecentChanged;

    private void OnRecentChanged()
    {
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从 <c>recent.jsonl</c> 加载最近访问列表。文件缺失或行损坏返回空列表 (不抛异常)。
    /// </summary>
    private List<RecentEntry> Load()
    {
        var result = new List<RecentEntry>();
        if (!File.Exists(_filePath)) return result;

        try
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var dto = JsonSerializer.Deserialize<RecentEntryDto>(line, JsonOptions);
                    if (dto is null) continue;
                    if (string.IsNullOrEmpty(dto.Path)) continue;
                    if (dto.Timestamp is null) continue;
                    result.Add(new RecentEntry(dto.Path, dto.Timestamp.Value));
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
            result.Clear();
        }

        // 防御性裁剪: 文件外部写入可能超出容量。
        while (result.Count > _maxEntries)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// 把内存中的全部记录整体重写到 recent.jsonl。自动创建父目录。
    /// 调用方必须已持有 <see cref="_lock"/>。
    /// </summary>
    private void Persist()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = new List<string>(_entries.Count);
        foreach (var entry in _entries)
        {
            var dto = new RecentEntryDto
            {
                Path = entry.Path,
                Timestamp = entry.Timestamp,
            };
            lines.Add(JsonSerializer.Serialize(dto, JsonOptions));
        }
        File.WriteAllLines(_filePath, lines);
    }

    /// <summary>
    /// JSONL 序列化 DTO。字段名按 ADR-0028 §7 规范使用 <c>path</c> 和 <c>ts</c>。
    /// </summary>
    private sealed class RecentEntryDto
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("ts")]
        public DateTimeOffset? Timestamp { get; set; }
    }
}
