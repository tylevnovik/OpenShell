namespace OpenShell.Clipboard;

/// <summary>
/// 剪贴板历史条目数据类型。Per ADR-0029 §13.
/// 区分纯文本写入与项引用写入, 影响 <see cref="ClipboardHistoryEntry.Data"/> 的运行时类型。
/// </summary>
public enum ClipboardDataKind
{
    /// <summary>纯文本 (来自 <c>SetTextAsync</c>)。Data 为 <see cref="string"/>。</summary>
    Text = 0,

    /// <summary>项引用 (来自 <c>SetItemsAsync</c>)。Data 为 <see cref="System.Collections.Generic.IReadOnlyList{T}"/>。</summary>
    Items = 1,
}
