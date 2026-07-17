using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Format-List</c> 命令：把上游 IItem 流渲染为列表（每属性一行）。
/// Pipeline Sink；ExecuteAsync 抛 NotSupportedException。
/// </summary>
[Verb("Format", Noun = "List", Aliases = ["fl", "list"])]
[Description("Formats items as a list of properties.")]
public sealed class FormatListCommand : IPipelineSink<FormatListCommand.Args>
{
    /// <param name="Properties">要显示的列名；null 或空表示用标准字段 + Properties.Keys。</param>
    /// <param name="Rows">最大行数。</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Properties = null,
        [property: Parameter(Aliases = new[] { "-r" })] int? Rows = null);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var formatter = new ListFormatter();
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
            Kind = ViewKind.List,
            MaxRows = args.Rows,
        };
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Format-List is pipeline-only.");
}
