using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Out-Default</c> 命令：默认输出 Sink。Per ADR-0011 §7.
/// CLI：调 TableFormatter，列取 Item 标准 5 字段（Name/Kind/Size/Modified/Path）。
/// GUI：调 GridFormatter（M3 实现，当前退化到 TableFormatter）。
/// PipelineExecutor 在末节点非 Sink 时调用 Host.WriteItemsAsync；
/// 用户也可显式 <c>... | out-default</c> 触发本命令。
/// </summary>
[Verb("Out", Noun = "Default", Aliases = ["od"])]
[Description("Default output sink: renders items as table in CLI, list in GUI.")]
public sealed class OutDefaultCommand : IPipelineSink<OutDefaultCommand.Args>
{
    public record Args;

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var formatter = new TableFormatter();
        var spec = new ViewSpec
        {
            Columns = ItemValueAccessor.StandardColumns(),
        };
        await formatter.FormatAsync(input, spec, ctx.Host, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Out-Default is pipeline-only.");
}
