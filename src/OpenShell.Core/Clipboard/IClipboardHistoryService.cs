namespace OpenShell.Clipboard;

/// <summary>
/// 剪贴板历史服务抽象。Per ADR-0029 §13.
/// 可选功能: 记录最近 N 次剪贴板操作, 供 <c>Win+V</c> 弹出选择面板重粘贴。
/// 实现订阅 <see cref="IClipboardService.ClipboardChanged"/> 自动追加历史, 并持久化到
/// <c>~/.openshell/clipboard-history.jsonl</c> (JSON Lines, 0600)。
/// </summary>
public interface IClipboardHistoryService
{
    /// <summary>
    /// 获取当前历史快照, 按时间倒序 (最新在前)。返回的是只读副本, 外部修改不影响内部缓冲。
    /// </summary>
    IReadOnlyList<ClipboardHistoryEntry> GetHistory();

    /// <summary>
    /// 清空历史: 清空内存环形缓冲并截断持久化文件 (truncate to 0 字节)。
    /// </summary>
    void Clear();

    /// <summary>
    /// 新条目追加到历史时触发 (在内存缓冲已更新、文件已追加后)。
    /// 事件参数为刚加入的 <see cref="ClipboardHistoryEntry"/>。
    /// </summary>
    event EventHandler<ClipboardHistoryEntry>? EntryAdded;
}
