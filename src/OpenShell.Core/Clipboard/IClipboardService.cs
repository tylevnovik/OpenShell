using OpenShell.Items;

namespace OpenShell.Clipboard;

/// <summary>
/// 剪贴板抽象。Per ADR-0029 §1 / §4.
/// 抽象 OS 剪贴板以支持文件对象复制/剪切、文本复制、跨 OpenShell 实例粘贴。
/// Cut 标记粘贴后清除 (见 ADR-0029 §4 约束)。
/// </summary>
public interface IClipboardService
{
    /// <summary>写入项引用 (含 cut 标记)。</summary>
    ValueTask SetItemsAsync(IReadOnlyList<IItem> items, bool cut = false, CancellationToken ct = default);

    /// <summary>读取项; 若为 cut 模式, 粘贴后清除剪贴板。无项时返回 null。</summary>
    ValueTask<IReadOnlyList<IItem>?> GetItemsAsync(CancellationToken ct = default);

    /// <summary>写入纯文本。</summary>
    ValueTask SetTextAsync(string text, CancellationToken ct = default);

    /// <summary>读取纯文本; 无文本时返回 null。</summary>
    ValueTask<string?> GetTextAsync(CancellationToken ct = default);

    /// <summary>当前剪贴板是否持有项引用。</summary>
    bool HasItems { get; }

    /// <summary>最近一次 SetItemsAsync 的 cut 标记; 无项时为 false。</summary>
    bool WasCut { get; }

    /// <summary>
    /// 剪贴板内容变更事件。Per ADR-0029 §13.
    /// 在 <see cref="SetItemsAsync"/> / <see cref="SetTextAsync"/> 成功写入后触发,
    /// 携带新条目快照供 <see cref="IClipboardHistoryService"/> 追加历史。
    /// </summary>
    event EventHandler<ClipboardHistoryEntry>? ClipboardChanged;
}
