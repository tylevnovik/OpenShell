using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Items;
using OpenShell.Interop;

namespace OpenShell.Clipboard;

/// <summary>
/// 进程内 <see cref="IClipboardHistoryService"/> 实现。Per ADR-0029 §13.
/// 维护最近 N 条剪贴板条目的环形缓冲 (默认 20), 持久化到
/// <c>~/.openshell/clipboard-history.jsonl</c> (JSON Lines, append-only, 0600 on Unix)。
/// </summary>
/// <remarks>
/// 构造时订阅 <see cref="IClipboardService.ClipboardChanged"/> 自动追加历史;
/// 历史文件可能含敏感数据 (复制的密码/密钥路径), 故 Unix 下设为 0600。
/// 环形缓冲使用 <see cref="LinkedList{T}"/> (First = 最新, Last = 最旧), 超容量时丢弃最旧。
/// </remarks>
public sealed class ClipboardHistoryService : IClipboardHistoryService
{
    private const int DefaultCapacity = 20;

    private readonly IClipboardService _clipboard;
    private readonly int _capacity;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly LinkedList<ClipboardHistoryEntry> _entries = new();

    /// <summary>
    /// 构造 ClipboardHistoryService 并加载已有历史。
    /// </summary>
    /// <param name="clipboard">剪贴板服务, 订阅其 <see cref="IClipboardService.ClipboardChanged"/> 事件。</param>
    /// <param name="capacity">环形缓冲容量, 默认 20 (Per ADR-0029 §13)。</param>
    public ClipboardHistoryService(IClipboardService clipboard, int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量必须为正数。");
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _capacity = capacity;
        _filePath = OpenShellPaths.ClipboardHistory;

        OpenShellPaths.EnsureClipboardDirs();
        LoadFromFile();
        EnsureFilePermissions();

        // 订阅剪贴板变更: 自动追加历史。事件在剪贴板写入线程同步触发, handler 内做小量文件追加。
        _clipboard.ClipboardChanged += OnClipboardChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<ClipboardHistoryEntry> GetHistory()
    {
        lock (_gate)
        {
            // 倒序快照 (最新在前)。ToArrayList 复制一份, 外部修改不影响内部缓冲。
            var snapshot = new ClipboardHistoryEntry[_entries.Count];
            var node = _entries.First;
            for (var i = 0; i < snapshot.Length && node is not null; i++, node = node.Next)
            {
                snapshot[i] = node.Value;
            }
            return snapshot;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            // 截断持久化文件为 0 字节。文件不存在时仅创建空文件 (Clear 语义)。
            try
            {
                if (File.Exists(_filePath))
                {
                    File.WriteAllText(_filePath, string.Empty);
                }
                else
                {
                    File.Create(_filePath).Dispose();
                    EnsureFilePermissions();
                }
            }
            catch (IOException)
            {
                // 历史是可选功能, 磁盘错误不应让 Clear 抛出影响主流程。内存缓冲已清空。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上: 权限不足时仅内存清空。
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ClipboardHistoryEntry>? EntryAdded;

    /// <summary>
    /// <see cref="IClipboardService.ClipboardChanged"/> 处理: 追加条目到环形缓冲 + 持久化文件 + 触发 <see cref="EntryAdded"/>。
    /// </summary>
    private void OnClipboardChanged(object? sender, ClipboardHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            // 环形缓冲: 新条目加入头部, 超容量时丢弃尾部 (最旧)。
            _entries.AddFirst(entry);
            while (_entries.Count > _capacity)
            {
                _entries.RemoveLast();
            }

            AppendToFile(entry);
        }

        // 在锁外触发事件, 避免订阅者回调内再次获取锁导致死锁。
        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>加载已有历史文件到内存缓冲 (倒序: 文件首行 = 最旧, 末行 = 最新)。</summary>
    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var lines = File.ReadAllLines(_filePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ClipboardHistoryEntry? entry;
                try
                {
                    entry = TryDeserializeEntry(line);
                }
                catch (JsonException)
                {
                    // 跳过损坏的单行, 保留其余可用历史。
                    continue;
                }
                if (entry is null) continue;

                // 文件顺序为追加顺序 (旧→新), AddLast 保持时序, 缓冲满后丢弃最旧。
                _entries.AddLast(entry);
            }

            // 文件可能超出当前容量 (容量被调小后), 截断到容量。
            while (_entries.Count > _capacity)
            {
                _entries.RemoveFirst();
            }
        }
        catch (IOException)
        {
            // 历史加载失败不阻塞服务启动, 退化为空历史。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>追加单条历史到 JSONL 文件 (一行一 JSON)。文件不存在时创建并设 0600。</summary>
    private void AppendToFile(ClipboardHistoryEntry entry)
    {
        try
        {
            var created = !File.Exists(_filePath);
            using var writer = new StreamWriter(_filePath, append: true);
            var record = ToRecord(entry);
            writer.WriteLine(JsonSerializer.Serialize(record, IpcMessageJsonContext.Options));
            writer.Flush();
            if (created)
            {
                EnsureFilePermissions();
            }
        }
        catch (IOException)
        {
            // 历史是可选功能, 磁盘错误不影响剪贴板主流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>在 Unix 上将历史文件权限设为 0600 (仅属主读写)。Windows / 其他平台 no-op。</summary>
    private void EnsureFilePermissions()
    {
        // OperatingSystem.IsUnix() 不存在于 .NET BCL, 等价语义为 !OperatingSystem.IsWindows()。
        if (OperatingSystem.IsWindows()) return;
        if (!File.Exists(_filePath)) return;
        try
        {
            // .NET 7+: UnixFileMode 仅在 Unix 平台可用。UserRead|UserWrite = 0600。
            File.SetUnixFileMode(_filePath, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // 权限设置失败不阻塞历史功能。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>把历史条目转为可 JSON 持久化的 DTO。Items 用 OpenShellItems JSON 内嵌, Text 直接存。</summary>
    private static HistoryRecord ToRecord(ClipboardHistoryEntry entry)
    {
        return entry.Kind switch
        {
            ClipboardDataKind.Items when entry.Data is IReadOnlyList<IItem> items => new HistoryRecord
            {
                Timestamp = entry.Timestamp,
                Kind = nameof(ClipboardDataKind.Items),
                DisplayText = entry.DisplayText,
                ItemsJson = ClipboardData.SerializeItems(items, cut: false),
            },
            ClipboardDataKind.Text when entry.Data is string text => new HistoryRecord
            {
                Timestamp = entry.Timestamp,
                Kind = nameof(ClipboardDataKind.Text),
                DisplayText = entry.DisplayText,
                Text = text,
            },
            _ => new HistoryRecord
            {
                Timestamp = entry.Timestamp,
                Kind = entry.Kind.ToString(),
                DisplayText = entry.DisplayText,
            },
        };
    }

    /// <summary>从 JSONL 单行反序列化为历史条目。无法识别的 Kind 返回 null。</summary>
    private static ClipboardHistoryEntry? TryDeserializeEntry(string line)
    {
        var record = JsonSerializer.Deserialize<HistoryRecord>(line, IpcMessageJsonContext.Options);
        if (record is null) return null;

        var kind = Enum.TryParse<ClipboardDataKind>(record.Kind, ignoreCase: false, out var k)
            ? k
            : ClipboardDataKind.Text;

        object? data = kind switch
        {
            ClipboardDataKind.Items when !string.IsNullOrEmpty(record.ItemsJson)
                => ClipboardData.DeserializeItems(record.ItemsJson).Items,
            ClipboardDataKind.Text => record.Text,
            _ => null,
        };

        var displayText = record.DisplayText ?? string.Empty;
        return new ClipboardHistoryEntry(record.Timestamp, displayText, kind, data);
    }

    /// <summary>JSONL 持久化 DTO。Items 序列化为 OpenShellItems JSON 内嵌于 ItemsJson; Text 直接存。</summary>
    private sealed class HistoryRecord
    {
        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("displayText")]
        public string DisplayText { get; set; } = string.Empty;

        [JsonPropertyName("itemsJson")]
        public string? ItemsJson { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
