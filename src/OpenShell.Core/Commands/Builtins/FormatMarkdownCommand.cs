using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Format-Markdown</c> 命令：把上游 IItem 流渲染为 GitHub Flavored Markdown 表格。
/// Pipeline Sink；ExecuteAsync 抛 NotSupportedException。Per ADR-0011 §7.
/// </summary>
[Verb("Format", Noun = "Markdown", Aliases = ["fmd", "md", "markdown"])]
[Description("Formats items as a GitHub Flavored Markdown table.")]
public sealed class FormatMarkdownCommand : IPipelineSink<FormatMarkdownCommand.Args>
{
    /// <param name="Properties">要显示的列名；null 或空表示自动发现（首项 Properties.Keys + 标准字段）。</param>
    /// <param name="Rows">最大行数。</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Properties = null,
        [property: Parameter(Aliases = new[] { "-r" })] int? Rows = null);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var formatter = new MarkdownFormatter();
        var columns = args.Properties is { Length: > 0 } props
            ? props.Select(p => new ColumnSpec { Name = p }).ToArray()
            : Array.Empty<ColumnSpec>();
        var spec = new ViewSpec
        {
            Columns = columns,
            Kind = ViewKind.Markdown,
            MaxRows = args.Rows,
        };
        await formatter.FormatAsync(input, spec, ctx.Host, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Format-Markdown is pipeline-only.");
}
