using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Items;
using OpenShell.Interop;
using OpenShell.Paths;

namespace OpenShell.Clipboard;

/// <summary>
/// 剪贴板数据序列化工具。Per ADR-0029 §2 / §3.
/// 提供 <c>OpenShellItems</c> 自定义格式 (JSON, 含 items + wasCut + timestamp) 与跨应用文本格式
/// (<c>text/uri-list</c> 仅 fs:: 路径, <c>text/plain</c> ItemPath.Display) 之间的转换。
/// IItem 序列化复用 <see cref="IpcMessageJsonContext.Options"/> 中的 IpcItemConverter, 避免修改 core domain 类型。
/// </summary>
public static class ClipboardData
{
    private const string FsProvider = "fs";

    /// <summary>序列化项列表为 OpenShellItems JSON 格式。</summary>
    public static string SerializeItems(IReadOnlyList<IItem> items, bool cut)
    {
        var payload = new ClipboardPayload
        {
            Items = items.ToList(),
            WasCut = cut,
            Timestamp = DateTimeOffset.UtcNow,
        };
        return JsonSerializer.Serialize(payload, IpcMessageJsonContext.Options);
    }

    /// <summary>反序列化 OpenShellItems JSON 为 (items, wasCut)。</summary>
    /// <exception cref="JsonException">JSON 格式非法或缺少必需字段。</exception>
    public static (IReadOnlyList<IItem> Items, bool WasCut) DeserializeItems(string json)
    {
        var payload = JsonSerializer.Deserialize<ClipboardPayload>(json, IpcMessageJsonContext.Options)
            ?? throw new JsonException("Clipboard payload is null.");
        var items = (IReadOnlyList<IItem>?)payload.Items ?? Array.Empty<IItem>();
        return (items, payload.WasCut);
    }

    /// <summary>
    /// 生成 text/uri-list 格式: 每行一个本地路径 (无 provider 前缀), 仅含 fs:: 项。
    /// 跨应用拖拽 (Explorer / Finder) 仅支持本地 fs 路径 (ADR-0029 §14 约束)。
    /// </summary>
    public static string ToUriList(IReadOnlyList<IItem> items)
    {
        var lines = items
            .Where(i => i.Path.Provider == FsProvider)
            .Select(i => i.Path.InternalPath);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 生成 text/plain 格式: 每行一个 ItemPath.Display (含 provider:: 前缀), 含所有 provider。
    /// 粘贴到文本框得此格式, CLI 可直接 paste | get-item 解析 (ADR-0029 §3)。
    /// </summary>
    public static string ToPlainText(IReadOnlyList<IItem> items)
    {
        var lines = items.Select(i => i.Path.Display);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 解析 text/uri-list 为 ItemPath 列表。每行视为本地路径, 包装为 fs:: ItemPath。
    /// 支持 file:// URI 与裸路径; 空行与以 # 开头的注释行 (RFC 2483) 被忽略。
    /// </summary>
    public static IReadOnlyList<ItemPath> TryParseUriList(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<ItemPath>();

        var result = new List<ItemPath>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#') continue;

            var internalPath = ExtractLocalPath(line);
            result.Add(new ItemPath { Provider = FsProvider, InternalPath = internalPath });
        }
        return result;
    }

    private static string ExtractLocalPath(string line)
    {
        const string fileScheme = "file://";
        if (line.StartsWith(fileScheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = line[fileScheme.Length..];
            // file://localhost/path → /path, file:///path → /path
            if (rest.Length > 0 && rest[0] == '/')
            {
                // Windows: file:///C:/path → C:/path (去掉前导 /, 保留驱动器字母)。
                if (rest.Length >= 3 && char.IsLetter(rest[1]) && rest[2] == ':')
                {
                    return rest[1..].Replace('\\', '/');
                }
                // Unix: file:///home/path → /home/path (保留前导 /)。
                return rest.Replace('\\', '/');
            }
            // file://host/path → 跨主机路径, 简化为去掉 host 部分。
            var slash = rest.IndexOf('/');
            return slash < 0 ? rest.Replace('\\', '/') : rest[slash..].Replace('\\', '/');
        }
        return line.Replace('\\', '/');
    }

    private sealed class ClipboardPayload
    {
        [JsonPropertyName("items")]
        public List<IItem>? Items { get; set; }

        [JsonPropertyName("wasCut")]
        public bool WasCut { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset? Timestamp { get; set; }
    }
}
