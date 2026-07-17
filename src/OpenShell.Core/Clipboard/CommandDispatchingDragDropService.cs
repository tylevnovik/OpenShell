using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Clipboard;

/// <summary>
/// 通过命令分发器实现 <see cref="IDragDropService"/>。Per ADR-0029 §8.
/// <see cref="AcceptDropAsync"/> 将拖拽效果转换为 copy-item / move-item / remove-item 命令,
/// 走命令分发器自动获得 Undo/Redo (ADR-0020) 与进度反馈 (ADR-0014)。
/// 实际命令调用通过注入的委托进行, 避免直接依赖 CliHost。
/// </summary>
/// <remarks>
/// TODO(ADR-0029 §7 / §9): AvaloniaDragDropService (OLE / XDnd / NSPasteboard 互操作) 由 Gui.Host 后续实现,
/// 需构建多格式 DataObject (OpenShellItems + text/uri-list + CF_HDROP) 并与 OS 拖拽源/目标交互。
/// </remarks>
public sealed class CommandDispatchingDragDropService : IDragDropService
{
    private readonly Func<string, CommandContext, CancellationToken, Task<IAsyncEnumerable<IItem>>> _dispatcher;
    private readonly Func<CommandContext> _contextFactory;

    /// <param name="dispatcher">命令分发器: 接受命令行 + 上下文 + 取消令牌, 返回命令产生的项流。</param>
    /// <param name="contextFactory">构造每次拖拽使用的 CommandContext (避免直接依赖 CliHost)。</param>
    public CommandDispatchingDragDropService(
        Func<string, CommandContext, CancellationToken, Task<IAsyncEnumerable<IItem>>> dispatcher,
        Func<CommandContext> contextFactory)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Core 层仅作为命令分发的入口。实际 OS 拖拽源 (鼠标光标 / 浮动缩略图 / 目标高亮)
    /// 由 AvaloniaDragDropService 在 GUI host 中实现。
    /// </remarks>
    public Task StartDragAsync(IReadOnlyList<IItem> items, ItemPath? target, DragDropEffects effects, CancellationToken ct)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (effects == DragDropEffects.None) return Task.CompletedTask;
        // 无 OS 互操作: 仅作为接口契约的占位, 实际拖拽源由 GUI host 接管。
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<DragDropEffects> AcceptDropAsync(
        ItemPath target, IReadOnlyList<IItem> items, DragDropEffects effect, CancellationToken ct)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (effect == DragDropEffects.None) return DragDropEffects.None;
        if (items.Count == 0) return effect;

        var cmd = effect switch
        {
            DragDropEffects.Copy => "copy-item",
            DragDropEffects.Move => "move-item",
            DragDropEffects.Delete => "remove-item",
            _ => throw new ArgumentOutOfRangeException(nameof(effect), $"Unsupported effect: {effect}"),
        };

        var ctx = _contextFactory();
        foreach (var item in items)
        {
            // remove-item 不需要目标参数; copy-item / move-item 接受 source + destination。
            var line = effect == DragDropEffects.Delete
                ? $"{cmd} {item.Path.Display}"
                : $"{cmd} {item.Path.Display} {target.Display}";

            var stream = await _dispatcher(line, ctx, ct).ConfigureAwait(false);
            // 消费 IAsyncEnumerable 以驱动命令实际执行 (命令体是 lazy 评估)。
            await foreach (var _ in stream.WithCancellation(ct).ConfigureAwait(false))
            {
                // 拖拽场景不消费命令产生的项, 仅驱动执行至完成。
            }
        }

        return effect;
    }
}
