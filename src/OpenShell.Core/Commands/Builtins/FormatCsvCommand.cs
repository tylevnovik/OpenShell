using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Format-Csv</c> 命令：把上游 IItem 流序列化为 CSV（首行表头，后续每行一个 Item）。
/// Pipeline Sink；ExecuteAsync 抛 NotSupportedException。
/// </summary>
[Verb("Format", Noun = "Csv", Aliases = ["fcsv", "csv"])]
[Description("Formats items as CSV (header + rows).")]
public sealed class FormatCsvCommand : IPipelineSink<FormatCsvCommand.Args>
{
    /// <param name="Properties">要显示的列名；null 或空表示自动发现（首项 Properties.Keys）。</param>
    /// <param name="Rows">最大行数。</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Properties = null,
        [property: Parameter(Aliases = new[] { "-r" })] int? Rows = null);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var formatter = new CsvFormatter();
        var columns = args.Properties is { Length: > 0 } props
            ? props.Select(p => new ColumnSpec { Name = p }).ToArray()
            : Array.Empty<ColumnSpec>();
        var spec = new ViewSpec
        {
            Columns = columns,
            Kind = ViewKind.Csv,
            MaxRows = args.Rows,
        };
        await formatter.FormatAsync(input, spec, ctx.Host, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Format-Csv is pipeline-only.");
}
