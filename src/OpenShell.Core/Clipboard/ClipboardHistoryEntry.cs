using OpenShell.Items;

namespace OpenShell.Clipboard;

/// <summary>
/// 剪贴板历史条目 (不可变快照)。Per ADR-0029 §13.
/// 由 <see cref="IClipboardService.ClipboardChanged"/> 事件携带, 并由
/// <see cref="IClipboardHistoryService"/> 持久化到 <c>~/.openshell/clipboard-history.jsonl</c>。
/// </summary>
/// <param name="Timestamp">写入时间 (UTC)。</param>
/// <param name="DisplayText">
/// 用户可见的展示文本。项引用: 各 <c>ItemPath.Display</c> 换行拼接 (<see cref="ClipboardData.ToPlainText"/>);
/// 纯文本: 原文。
/// </param>
/// <param name="Kind">数据类型, 决定 <paramref name="Data"/> 的运行时类型。</param>
/// <param name="Data">
/// 原始数据: <see cref="ClipboardDataKind.Items"/> 时为 <see cref="IReadOnlyList{T}"/>;
/// <see cref="ClipboardDataKind.Text"/> 时为 <see cref="string"/>。
/// </param>
public sealed record ClipboardHistoryEntry(
    DateTimeOffset Timestamp,
    string DisplayText,
    ClipboardDataKind Kind,
    object? Data);
