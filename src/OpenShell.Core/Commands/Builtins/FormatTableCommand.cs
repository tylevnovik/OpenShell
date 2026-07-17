using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Format-Table</c> 命令：把上游 IItem 流渲染为 ASCII 表格。Per ADR-0011.
/// Pipeline Sink，仅可在管道末节点调用；ExecuteAsync 抛 NotSupportedException。
/// </summary>
[Verb("Format", Noun = "Table", Aliases = ["ft", "table"])]
[Description("Formats items as an ASCII table.")]
public sealed class FormatTableCommand : IPipelineSink<FormatTableCommand.Args>
{
    /// <summary>Format-Table 参数。</summary>
    /// <param name="Properties">要显示的列名；null 或空表示自动发现。</param>
    /// <param name="AutoSize">是否按内容自适应列宽（M2 与默认行为相同）。</param>
    /// <param name="Rows">最大行数；null 不限制。</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Properties = null,
        [property: Parameter(Aliases = new[] { "-a" })] bool AutoSize = false,
        [property: Parameter(Aliases = new[] { "-r" })] int? Rows = null);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var formatter = new TableFormatter();
        var spec = BuildSpec(args);
        await formatter.FormatAsync(input, spec, ctx.Host, cancellationToken).ConfigureAwait(false);
    }

    private static ViewSpec BuildSpec(Args args)
    {
        var columns = args.Properties is { Length: > 0 } props
            ? props.Select(p => new ColumnSpec { Name = p }).ToArray()
            : Array.Empty<ColumnSpec>();

        return new ViewSpec
        {
            Columns = columns,
            Kind = ViewKind.Table,
            MaxRows = args.Rows,
        };
    }

    /// <summary>Format-Table 仅可在管道中调用，不可单独执行。</summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Format-Table is pipeline-only.");
}
