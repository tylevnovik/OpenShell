using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using OpenShell.Clipboard;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Gui.Host.Services;

/// <summary>
/// Avalonia 实现 <see cref="IClipboardService"/>: OS 剪贴板互操作。Per ADR-0029 §2.
/// 写入时同时设置 <c>OpenShellItems</c> (自定义 JSON) + <c>text/uri-list</c> + <c>text/plain</c> +
/// <c>CF_HDROP</c> (Windows 文件列表, 经 Avalonia <c>DataFormats.FileDrop</c> 由 Win32 后端构建 DROPFILES)。
/// 读取时优先 <c>OpenShellItems</c>, 回退 <c>text/uri-list</c> / <c>text/plain</c> / <c>FileDrop</c>。
/// 所有 Avalonia 调用做 <see cref="Application.Current"/> null 防护 (测试 / UI 未就绪场景安全退化)。
/// </summary>
internal sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly object _gate = new();
    private IReadOnlyList<IItem>? _lastItems;
    private bool _wasCut;

    /// <inheritdoc />
    public async ValueTask SetItemsAsync(IReadOnlyList<IItem> items, bool cut = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();

        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            // UI 未就绪 (测试 / 无窗口): 仅更新进程内缓存, 不触发 OS 写入。
            lock (_gate)
            {
                _lastItems = items;
                _wasCut = cut;
            }
            return;
        }

        var data = BuildDataObject(items, cut);
        await clipboard.SetDataObjectAsync(data).ConfigureAwait(false);

        lock (_gate)
        {
            _lastItems = items;
            _wasCut = cut;
        }

        RaiseClipboardChanged(new ClipboardHistoryEntry(
            DateTimeOffset.UtcNow,
            ClipboardData.ToPlainText(items),
            ClipboardDataKind.Items,
            items));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IItem>?> GetItemsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            lock (_gate) return _lastItems;
        }

        // 优先级 1: OpenShellItems (跨 OpenShell 实例粘贴, 含 wasCut 标记)。
        var json = await TryGetStringAsync(clipboard, "OpenShellItems").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var (items, _) = ClipboardData.DeserializeItems(json);
                await MaybeClearForCutAsync(clipboard).ConfigureAwait(false);
                return items;
            }
            catch (System.Text.Json.JsonException)
            {
                // 损坏的 OpenShellItems: 回退到文本格式。
            }
        }

        // 优先级 2: text/uri-list (跨应用, Linux XDnd / 裸路径)。
        var uriList = await TryGetStringAsync(clipboard, "text/uri-list").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(uriList))
        {
            var paths = ClipboardData.TryParseUriList(uriList);
            if (paths.Count > 0)
            {
                await MaybeClearForCutAsync(clipboard).ConfigureAwait(false);
                return WrapPaths(paths);
            }
        }

        // 优先级 3: FileDrop (Explorer → OpenShell, Windows CF_HDROP)。
        var fileDrop = await TryGetFileNamesAsync(clipboard).ConfigureAwait(false);
        if (fileDrop is { Count: > 0 })
        {
            await MaybeClearForCutAsync(clipboard).ConfigureAwait(false);
            return WrapPaths(fileDrop.Select(p => new ItemPath { Provider = "fs", InternalPath = p.Replace('\\', '/') }).ToArray());
        }

        // 优先级 4: text/plain (OpenShell Display 文本 fs::path)。
        var plain = await TryGetStringAsync(clipboard, "text/plain").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(plain))
        {
            var wrapped = WrapDisplayText(plain);
            if (wrapped.Count > 0)
            {
                await MaybeClearForCutAsync(clipboard).ConfigureAwait(false);
                return wrapped;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask SetTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ct.ThrowIfCancellationRequested();

        var clipboard = GetClipboard();
        if (clipboard is null) return;

        // SetTextAsync 仅写 text/plain (清空其他格式)。
        await clipboard.SetTextAsync(text).ConfigureAwait(false);

        lock (_gate)
        {
            _lastItems = null;
            _wasCut = false;
        }

        if (!string.IsNullOrEmpty(text))
        {
            RaiseClipboardChanged(new ClipboardHistoryEntry(
                DateTimeOffset.UtcNow, text, ClipboardDataKind.Text, text));
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var clipboard = GetClipboard();
        if (clipboard is null) return null;
        return await clipboard.GetTextAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool HasItems
    {
        get { lock (_gate) return _lastItems is { Count: > 0 }; }
    }

    /// <inheritdoc />
    public bool WasCut
    {
        get { lock (_gate) return _lastItems is not null && _wasCut; }
    }

    /// <inheritdoc />
    public event EventHandler<ClipboardHistoryEntry>? ClipboardChanged;

    private void RaiseClipboardChanged(ClipboardHistoryEntry entry)
        => ClipboardChanged?.Invoke(this, entry);

    /// <summary>
    /// 构建多格式 <see cref="IDataObject"/>。Per ADR-0029 §2.
    /// OpenShellItems: 跨 OpenShell 实例 (JSON, 含 wasCut); text/uri-list + text/plain: 跨应用 / 文本框;
    /// FileDrop: Windows Explorer 互操作 (Avalonia Win32 后端转 CF_HDROP/DROPFILES)。
    /// </summary>
    private static DataObject BuildDataObject(IReadOnlyList<IItem> items, bool cut)
    {
        var data = new DataObject();
        data.Set("OpenShellItems", ClipboardData.SerializeItems(items, cut));
        data.Set("text/uri-list", ClipboardData.ToUriList(items));
        data.Set("text/plain", ClipboardData.ToPlainText(items));

        // 仅本地 fs:: 项可跨应用拖拽到 Explorer (ADR-0029 §14 约束)。
        var fsPaths = items
            .Where(i => i.Path.Provider == "fs")
            .Select(i => ToNativeFilePath(i.Path.InternalPath))
            .ToArray();

        if (fsPaths.Length > 0)
        {
            // Avalonia DataFormats.FileNames (桌面平台): Win32 后端构建 DROPFILES (CF_HDROP), X11/macOS 后端转 uri-list/NSPasteboard。
            try
            {
                data.Set(DataFormats.FileNames, fsPaths);
            }
            catch (ArgumentException)
            {
                // 个别平台不接受 FileNames: 其他格式仍可用, 忽略此格式。
            }
        }

        return data;
    }

    /// <summary>
    /// Cut 模式粘贴后清除 OS 剪贴板 (ADR-0029 §4 约束)。仅当本进程曾以 cut 写入时生效。
    /// </summary>
    private async ValueTask MaybeClearForCutAsync(IClipboard clipboard)
    {
        bool shouldClear;
        lock (_gate)
        {
            shouldClear = _wasCut;
            _wasCut = false;
            _lastItems = null;
        }
        if (shouldClear)
        {
            await clipboard.ClearAsync().ConfigureAwait(false);
        }
    }

    /// <summary>安全读取剪贴板字符串格式, 失败返回 null。</summary>
    private static async Task<string?> TryGetStringAsync(IClipboard clipboard, string format)
    {
        try
        {
            var obj = await clipboard.GetDataAsync(format).ConfigureAwait(false);
            return ToStringData(obj);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>读取 FileNames 格式为文件名列表 (Explorer 互操作)。失败/无数据返回 null。</summary>
    private static async Task<IReadOnlyList<string>?> TryGetFileNamesAsync(IClipboard clipboard)
    {
        try
        {
            var obj = await clipboard.GetDataAsync(DataFormats.FileNames).ConfigureAwait(false);
            return obj switch
            {
                IEnumerable<string> paths => paths.ToList(),
                string single => new[] { single },
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>把剪贴板数据对象转为字符串 (string / Stream / byte[] 兼容)。</summary>
    private static string? ToStringData(object? obj)
    {
        return obj switch
        {
            null => null,
            string s => s,
            System.IO.Stream stream => ReadStreamAsString(stream),
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => obj.ToString(),
        };
    }

    private static string ReadStreamAsString(System.IO.Stream stream)
    {
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        return reader.ReadToEnd();
    }

    /// <summary>把 ItemPath 列表包装为最小 <see cref="IItem"/> (Kind=Unknown, 由接收命令解析实际类型)。</summary>
    private static IReadOnlyList<IItem> WrapPaths(IEnumerable<ItemPath> paths)
    {
        var list = new List<IItem>();
        foreach (var p in paths)
        {
            list.Add(new Item { Path = p, Kind = ItemKind.Unknown });
        }
        return list;
    }

    /// <summary>把 text/plain (每行一个 ItemPath.Display) 解析为 IItem 列表。无法解析的行跳过。</summary>
    private static IReadOnlyList<IItem> WrapDisplayText(string plain)
    {
        var list = new List<IItem>();
        foreach (var raw in plain.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            // 跳过明显非路径文本 (避免把任意粘贴文本当路径)。
            if (!line.Contains("::", StringComparison.Ordinal) && !IsLikelyLocalPath(line)) continue;
            try
            {
                list.Add(new Item { Path = ItemPath.Parse(line), Kind = ItemKind.Unknown });
            }
            catch (ArgumentException)
            {
                // 单行解析失败: 跳过, 保留可解析行。
            }
        }
        return list;
    }

    private static bool IsLikelyLocalPath(string line)
    {
        // Unix 绝对路径或 Windows 盘符路径视为本地路径候选。
        return (line.Length > 0 && line[0] == '/')
            || (line.Length >= 2 && char.IsLetter(line[0]) && line[1] == ':');
    }

    /// <summary>把 InternalPath (统一用 '/') 转为 OS 原生文件路径分隔符。</summary>
    private static string ToNativeFilePath(string internalPath)
        => internalPath.Replace('/', System.IO.Path.DirectorySeparatorChar);

    /// <summary>
    /// 懒解析 OS 剪贴板: 取 MainWindow (TopLevel) 的 Clipboard。
    /// DI 容器在 Avalonia Application 启动前就构建, 此时 MainWindow 可能尚未创建 → 返回 null。
    /// </summary>
    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            // Window 继承自 TopLevel, Toplevel.Clipboard 返回 IClipboard?。
            return window.Clipboard;
        }
        return null;
    }
}
