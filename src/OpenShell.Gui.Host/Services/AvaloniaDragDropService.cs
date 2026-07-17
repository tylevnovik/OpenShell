using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OpenShell.Clipboard;
using OpenShell.Items;
using OpenShell.Paths;

// 消歧 OpenShell.Clipboard.DragDropEffects 与 Avalonia.Input.DragDropEffects
// (本文件内 DragDropEffects 始终指代 OpenShell 的语义枚举; 调用 Avalonia API 时用 ToAvaloniaEffects 显式转换)
using DragDropEffects = OpenShell.Clipboard.DragDropEffects;

namespace OpenShell.Gui.Host.Services;

/// <summary>
/// Avalonia 实现 <see cref="IDragDropService"/>: OS 拖拽互操作 (OLE / XDnd / NSPasteboard)。Per ADR-0029 §7 / §9.
/// 拖拽源通过 <see cref="StartDragFromPointerAsync"/> 调用 <see cref="DragDrop.DoDragDrop"/> 启动 OS 拖拽,
/// 携带 4 格式 DataObject (OpenShellItems + text/uri-list + text/plain + CF_HDROP)。
/// 放置目标通过 <see cref="RegisterDropTarget"/> 注册, DragOver 协商效果 (Copy/Move/None), Drop 委托
/// <see cref="CommandDispatchingDragDropService.AcceptDropAsync"/> 转 copy-item/move-item/remove-item 命令。
/// </summary>
/// <remarks>
/// IDragDropService.StartDragAsync 抽象不携带 Avalonia <see cref="PointerEventArgs"/>,
/// 故仅做输入校验; 实际 OS 拖拽由 GUI 控件的 PointerPressed 处理器调用
/// <see cref="StartDragFromPointerAsync"/> 完成 (与 CommandDispatchingDragDropService 占位语义一致)。
/// Per ADR-0029 §14: 仅本地 fs:: 路径可跨应用拖拽, 远程路径 (s3://) 仅文本。
/// </remarks>
internal sealed class AvaloniaDragDropService : IDragDropService
{
    private readonly CommandDispatchingDragDropService _inner;

    /// <summary>
    /// 构造 AvaloniaDragDropService。
    /// </summary>
    /// <param name="inner">
    /// 命令分发拖拽服务, <see cref="AcceptDropAsync"/> 委托给它转 copy-item/move-item/remove-item 命令。
    /// 走命令分发器自动获得 Undo/Redo (ADR-0020) 与进度反馈 (ADR-0014)。
    /// </param>
    public AvaloniaDragDropService(CommandDispatchingDragDropService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    /// <remarks>
    /// IDragDropService 抽象不携带 Avalonia <see cref="PointerEventArgs"/>, 故此方法仅做输入校验。
    /// 实际 OS 拖拽需 <see cref="PointerEventArgs"/> (来自 PointerPressed 事件),
    /// 由 GUI 控件调用 <see cref="StartDragFromPointerAsync"/> 完成。
    /// </remarks>
    public Task StartDragAsync(IReadOnlyList<IItem> items, ItemPath? target, DragDropEffects effects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (effects == DragDropEffects.None) return Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从 Avalonia PointerPressed 事件触发实际 OS 拖拽。Per ADR-0029 §7 / §9.
    /// GUI 控件的 PointerPressed 处理器应在判定为拖拽手势 (鼠标移动超过阈值) 后调用本方法。
    /// 构建 4 格式 DataObject (与剪贴板一致) 并调用 <see cref="DragDrop.DoDragDrop"/>。
    /// </summary>
    /// <param name="items">被拖拽的项列表。</param>
    /// <param name="trigger">触发拖拽的 PointerPressed/Moved 事件参数 (来自 Avalonia 事件)。</param>
    /// <param name="effects">源允许的拖拽效果 (Copy/Move/Link/Delete 组合)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际执行的拖拽效果 (None 表示用户取消或目标拒绝)。</returns>
    public async Task<DragDropEffects> StartDragFromPointerAsync(
        IReadOnlyList<IItem> items,
        PointerEventArgs trigger,
        DragDropEffects effects,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(trigger);
        if (effects == DragDropEffects.None) return DragDropEffects.None;
        if (items.Count == 0) return DragDropEffects.None;
        ct.ThrowIfCancellationRequested();

        var data = BuildDragDataObject(items, effects);

        // TODO(ADR-0029 §7): Avalonia 11 DragDrop.DoDragDrop 不支持自定义拖拽缩略图 (浮动首项 icon + 项数 badge)。
        // 视觉细节属非核心功能; 后续 Avalonia 版本若提供 drag visual API, 在此处附加 Adorner / Thumbnail。
        var allowed = ToAvaloniaEffects(effects);
        var actual = await DragDrop.DoDragDrop(trigger, data, allowed).ConfigureAwait(false);
        return FromAvaloniaEffects(actual);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 委托给 <see cref="CommandDispatchingDragDropService.AcceptDropAsync"/> 转 copy-item/move-item/remove-item 命令。
    /// 本方法供外部代码 (如 CLI 窗口接收路径文本) 直接调用; GUI 控件的 Drop 事件由
    /// <see cref="RegisterDropTarget"/> 注册的处理器内部调用 <c>_inner.AcceptDropAsync</c>。
    /// </remarks>
    public Task<DragDropEffects> AcceptDropAsync(
        ItemPath target, IReadOnlyList<IItem> items, DragDropEffects effect, CancellationToken ct)
    {
        return _inner.AcceptDropAsync(target, items, effect, ct);
    }

    /// <summary>
    /// 注册放置目标控件。Per ADR-0029 §7 / §9 / §14.
    /// 设置 <see cref="DragDrop.SetAllowDrop"/>, 订阅 DragOver (协商效果 + 光标) 与 Drop (转命令) 事件。
    /// DragOver 根据目标类型 (fs:: 目录 / Trash) 与修饰键 (Ctrl=Copy, Shift=Move, Alt=Link) 协商效果;
    /// 非有效目标设 <see cref="Avalonia.Input.DragDropEffects.None"/> 显示 "禁止" 光标。
    /// </summary>
    /// <param name="target">接受放置的 Avalonia 控件 (ListBox / TreeView / Trash widget 等)。</param>
    /// <param name="targetResolver">
    /// 解析当前放置目标 <see cref="ItemPath"/> 的回调; 返回 null 表示非目录目标 (如 Trash, 依赖 <paramref name="effectFilter"/>)。
    /// </param>
    /// <param name="effectFilter">
    /// 可选效果过滤器, 在默认协商后调用; Trash 目标用它返回 <see cref="DragDropEffects.Delete"/>。
    /// </param>
    public void RegisterDropTarget(
        Control target,
        Func<ItemPath?> targetResolver,
        Func<DragDropEffects, DragDropEffects>? effectFilter = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetResolver);

        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        target.AddHandler(DragDrop.DropEvent, OnDrop);

        // --- DragOver: 协商效果 + 光标 (同步处理, 避免 async void 在事件路由完成后才设 Handled) ---
        void OnDragOver(object? sender, DragEventArgs e)
        {
            var negotiated = ResolveEffect(targetResolver, effectFilter, e.Data, e.KeyModifiers);
            e.DragEffects = ToAvaloniaEffects(negotiated);
            e.Handled = true;
        }

        // --- Drop: 解析数据 → 委托 _inner.AcceptDropAsync 转命令 ---
        async void OnDrop(object? sender, DragEventArgs e)
        {
            // 同步设置 Handled, 阻止事件继续路由 (async void 首条语句)。
            e.Handled = true;

            var effect = ResolveEffect(targetResolver, effectFilter, e.Data, e.KeyModifiers);
            if (effect == DragDropEffects.None) return;

            var items = TryParseDropData(e.Data);
            if (items is null || items.Count == 0) return;

            // ADR-0029 §14: 跨应用拖拽 (无 OpenShellItems) 仅含本地 fs:: 路径, 已由 TryParseDropData 保证。
            // Trash 目标 (targetResolver 返回 null) 传 default ItemPath; Delete 效果下命令层忽略目标。
            var dropPath = targetResolver() ?? default;
            try
            {
                await _inner.AcceptDropAsync(dropPath, items, effect, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 拖拽操作被取消, 静默处理。
            }
        }
    }

    /// <summary>
    /// 协商放置效果。Per ADR-0029 §6 / §14.
    /// 目标非 fs:: → None; Trash (targetResolver 返回 null) → effectFilter 决定;
    /// 否则按修饰键 + 同/跨 Provider 协商 (同 Provider 默认 Move, 跨 Provider 默认 Copy)。
    /// </summary>
    private static DragDropEffects ResolveEffect(
        Func<ItemPath?> targetResolver,
        Func<DragDropEffects, DragDropEffects>? effectFilter,
        IDataObject data,
        KeyModifiers modifiers)
    {
        var requested = InferRequestedEffects(data);
        var dropPath = targetResolver();

        if (dropPath is null)
        {
            // 非目录目标 (如 Trash): 仅 effectFilter 可授权。
            return effectFilter?.Invoke(requested) ?? DragDropEffects.None;
        }

        if (dropPath.Value.Provider != "fs")
        {
            // ADR-0029 §14: 仅本地 fs:: 路径可接受文件放置。
            return DragDropEffects.None;
        }

        var negotiated = NegotiateByModifiers(requested, dropPath.Value, data, modifiers);
        return effectFilter?.Invoke(negotiated) ?? negotiated;
    }

    /// <summary>
    /// 按修饰键与 Provider 关系协商效果。Per ADR-0029 §6.
    /// Ctrl 强制 Copy, Shift 强制 Move, Alt 强制 Link; 无修饰键时同 Provider 默认 Move, 跨 Provider 默认 Copy。
    /// </summary>
    private static DragDropEffects NegotiateByModifiers(
        DragDropEffects requested, ItemPath target, IDataObject data, KeyModifiers modifiers)
    {
        var allowed = requested & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if (allowed == DragDropEffects.None) allowed = DragDropEffects.Copy | DragDropEffects.Move;

        if ((modifiers & KeyModifiers.Control) != 0)
            return (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if ((modifiers & KeyModifiers.Shift) != 0)
            return (allowed & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
        if ((modifiers & KeyModifiers.Alt) != 0)
            return (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.None;

        var sourceProvider = TryGetString(data, "OpenShellSourceProvider") ?? "fs";
        var sameProvider = sourceProvider == target.Provider;
        return sameProvider
            ? ((allowed & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.Copy)
            : ((allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None);
    }

    /// <summary>从 DataObject 读取源声明的允许效果 (OpenShellDragEffects 格式); 缺失时默认 Copy|Move。</summary>
    private static DragDropEffects InferRequestedEffects(IDataObject data)
    {
        var s = TryGetString(data, "OpenShellDragEffects");
        if (s is not null && Enum.TryParse<DragDropEffects>(s, ignoreCase: true, out var e))
            return e;
        return DragDropEffects.Copy | DragDropEffects.Move;
    }

    /// <summary>构建拖拽 DataObject (4 格式 + 源 Provider + 效果元数据)。与 AvaloniaClipboardService.BuildDataObject 一致。</summary>
    private static DataObject BuildDragDataObject(IReadOnlyList<IItem> items, DragDropEffects effects)
    {
        var data = new DataObject();
        data.Set("OpenShellItems", ClipboardData.SerializeItems(items, cut: false));
        data.Set("text/uri-list", ClipboardData.ToUriList(items));
        data.Set("text/plain", ClipboardData.ToPlainText(items));
        // 源允许效果 + 源 Provider: 供目标 DragOver 协商 (避免反序列化 items)。
        data.Set("OpenShellDragEffects", effects.ToString());
        var sourceProvider = items.Count > 0 ? items[0].Path.Provider : "fs";
        data.Set("OpenShellSourceProvider", sourceProvider);

        // CF_HDROP (Windows) / uri-list (Linux) / NSPasteboard (macOS): 仅本地 fs:: 项。
        var fsPaths = items
            .Where(i => i.Path.Provider == "fs")
            .Select(i => ToNativeFilePath(i.Path.InternalPath))
            .ToArray();
        if (fsPaths.Length > 0)
        {
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
    /// 从放置数据解析 <see cref="IItem"/> 列表。Per ADR-0029 §2 / §14.
    /// 优先级: OpenShellItems (跨 OpenShell 实例, 含任意 Provider) → text/uri-list (跨应用, 仅 fs::)
    /// → FileNames (Explorer, 仅 fs::) → text/plain (Display 文本, 任意 Provider)。
    /// </summary>
    private static IReadOnlyList<IItem>? TryParseDropData(IDataObject data)
    {
        // 优先级 1: OpenShellItems (跨 OpenShell 实例, 含 wasCut 标记)。
        var json = TryGetString(data, "OpenShellItems");
        if (!string.IsNullOrEmpty(json))
        {
            try { return ClipboardData.DeserializeItems(json).Items; }
            catch (System.Text.Json.JsonException) { /* 损坏: 回退 */ }
        }

        // 优先级 2: text/uri-list (跨应用, Linux XDnd / 裸路径)。
        var uriList = TryGetString(data, "text/uri-list");
        if (!string.IsNullOrEmpty(uriList))
        {
            var paths = ClipboardData.TryParseUriList(uriList);
            if (paths.Count > 0) return WrapPaths(paths);
        }

        // 优先级 3: FileNames (Explorer → OpenShell, Windows CF_HDROP)。
        var fileDrop = TryGetFileNames(data);
        if (fileDrop is { Count: > 0 })
        {
            return WrapPaths(fileDrop
                .Select(p => new ItemPath { Provider = "fs", InternalPath = p.Replace('\\', '/') })
                .ToArray());
        }

        // 优先级 4: text/plain (OpenShell Display 文本, 任意 Provider)。
        var plain = TryGetString(data, "text/plain");
        if (!string.IsNullOrEmpty(plain))
        {
            var wrapped = WrapDisplayText(plain);
            if (wrapped.Count > 0) return wrapped;
        }

        return null;
    }

    // --- OpenShell ↔ Avalonia 效果映射 ---

    /// <summary>
    /// OpenShell DragDropEffects → Avalonia DragDropEffects。
    /// Delete 无 Avalonia 对应, 映射为 Move (Trash 放置的视觉近似; 实际效果由 ResolveEffect 重算)。
    /// </summary>
    private static Avalonia.Input.DragDropEffects ToAvaloniaEffects(DragDropEffects e)
    {
        var result = Avalonia.Input.DragDropEffects.None;
        if ((e & DragDropEffects.Copy) != 0) result |= Avalonia.Input.DragDropEffects.Copy;
        if ((e & DragDropEffects.Move) != 0) result |= Avalonia.Input.DragDropEffects.Move;
        if ((e & DragDropEffects.Link) != 0) result |= Avalonia.Input.DragDropEffects.Link;
        if ((e & DragDropEffects.Delete) != 0) result |= Avalonia.Input.DragDropEffects.Move;
        return result;
    }

    /// <summary>Avalonia DragDropEffects → OpenShell DragDropEffects (Delete 不可逆, 需由 ResolveEffect 重算)。</summary>
    private static DragDropEffects FromAvaloniaEffects(Avalonia.Input.DragDropEffects e)
    {
        var result = DragDropEffects.None;
        if ((e & Avalonia.Input.DragDropEffects.Copy) != 0) result |= DragDropEffects.Copy;
        if ((e & Avalonia.Input.DragDropEffects.Move) != 0) result |= DragDropEffects.Move;
        if ((e & Avalonia.Input.DragDropEffects.Link) != 0) result |= DragDropEffects.Link;
        return result;
    }

    // --- IDataObject 读取助手 (同步, 与 IClipboard 异步版不同) ---

    private static string? TryGetString(IDataObject data, string format)
    {
        try
        {
            return ToStringData(data.Get(format));
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static IReadOnlyList<string>? TryGetFileNames(IDataObject data)
    {
        try
        {
            var obj = data.Get(DataFormats.FileNames);
            return obj switch
            {
                null => null,
                string[] arr => arr,
                IEnumerable<string> paths => paths.ToList(),
                string single => new[] { single },
                _ => null,
            };
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

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

    // --- IItem 包装助手 (与 AvaloniaClipboardService 一致; 小幅重复以避免跨类耦合) ---

    /// <summary>把 ItemPath 列表包装为最小 <see cref="IItem"/> (Kind=Unknown)。</summary>
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
            if (!line.Contains("::", StringComparison.Ordinal) && !IsLikelyLocalPath(line)) continue;
            try
            {
                list.Add(new Item { Path = ItemPath.Parse(line), Kind = ItemKind.Unknown });
            }
            catch (ArgumentException) { /* 单行解析失败: 跳过 */ }
        }
        return list;
    }

    private static bool IsLikelyLocalPath(string line)
        => (line.Length > 0 && line[0] == '/')
           || (line.Length >= 2 && char.IsLetter(line[0]) && line[1] == ':');

    private static string ToNativeFilePath(string internalPath)
        => internalPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
}
