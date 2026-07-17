using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Clipboard;

/// <summary>
/// 拖拽抽象。Per ADR-0029 §5 / §8.
/// 接收端通过 <see cref="AcceptDropAsync"/> 将拖拽效果转换为 copy-item / move-item / remove-item 命令,
/// 走命令分发器自动获得 Undo/Redo (ADR-0020) 与进度反馈 (ADR-0014)。
/// </summary>
public interface IDragDropService
{
    /// <summary>开始拖拽。target 是预期放置位置 (鼠标悬停的目录)。</summary>
    Task StartDragAsync(IReadOnlyList<IItem> items, ItemPath? target, DragDropEffects effects, CancellationToken ct);

    /// <summary>接收拖入。返回实际效果。</summary>
    Task<DragDropEffects> AcceptDropAsync(ItemPath target, IReadOnlyList<IItem> items, DragDropEffects effect, CancellationToken ct);
}
