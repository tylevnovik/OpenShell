using OpenShell.Items;

namespace OpenShell.Clipboard;

/// <summary>
/// 进程内 <see cref="IClipboardService"/> 实现, 不依赖 OS 剪贴板。Per ADR-0029 §1.
/// 用于 CLI host 与单元测试。Cut 模式下 <see cref="GetItemsAsync"/> 返回后清除剪贴板 (ADR-0029 §4 约束)。
/// </summary>
/// <remarks>
/// TODO(ADR-0029 §2): AvaloniaClipboardService (OS 剪贴板互操作) 由 Gui.Host 后续实现,
/// 需同时写入 OpenShellItems + text/uri-list + text/plain + CF_HDROP (Windows) 多格式。
/// </remarks>
public sealed class InMemoryClipboardService : IClipboardService
{
    private IReadOnlyList<IItem>? _items;
    private bool _wasCut;
    private string? _text;

    /// <inheritdoc />
    public ValueTask SetItemsAsync(IReadOnlyList<IItem> items, bool cut = false, CancellationToken ct = default)
    {
        _items = items;
        _wasCut = cut;
        // 写入 items 后清空纯文本槽位, 避免语义混淆 (SetItems 优先于 SetText)。
        _text = null;
        // ADR-0029 §13: 触发 ClipboardChanged 通知历史服务追加。仅非空项集合才有意义。
        if (items is { Count: > 0 })
        {
            RaiseClipboardChanged(new ClipboardHistoryEntry(
                DateTimeOffset.UtcNow,
                ClipboardData.ToPlainText(items),
                ClipboardDataKind.Items,
                items));
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IItem>?> GetItemsAsync(CancellationToken ct = default)
    {
        var items = _items;
        if (items is not null && _wasCut)
        {
            // ADR-0029 §4 约束: Cut 操作粘贴后必须清除剪贴板。
            _items = null;
            _wasCut = false;
        }
        return new ValueTask<IReadOnlyList<IItem>?>(items);
    }

    /// <inheritdoc />
    public ValueTask SetTextAsync(string text, CancellationToken ct = default)
    {
        _text = text;
        // 写入纯文本后清空 items 槽位, 保持 "纯文本" 与 "items" 互斥。
        _items = null;
        _wasCut = false;
        // ADR-0029 §13: 触发 ClipboardChanged 通知历史服务追加。空字符串仍记录 (用户可能有意清空文本槽)。
        if (!string.IsNullOrEmpty(text))
        {
            RaiseClipboardChanged(new ClipboardHistoryEntry(
                DateTimeOffset.UtcNow,
                text,
                ClipboardDataKind.Text,
                text));
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask<string?> GetTextAsync(CancellationToken ct = default)
    {
        return new ValueTask<string?>(_text);
    }

    /// <inheritdoc />
    public bool HasItems => _items is not null && _items.Count > 0;

    /// <inheritdoc />
    public bool WasCut => _items is not null && _wasCut;

    /// <inheritdoc />
    public event EventHandler<ClipboardHistoryEntry>? ClipboardChanged;

    /// <summary>触发 <see cref="ClipboardChanged"/> 事件。空订阅列表时为 no-op。</summary>
    private void RaiseClipboardChanged(ClipboardHistoryEntry entry)
        => ClipboardChanged?.Invoke(this, entry);
}
