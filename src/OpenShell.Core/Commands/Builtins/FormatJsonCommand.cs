using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Format-Json</c> 命令：把上游 IItem 流序列化为 JSON Lines。
/// Pipeline Sink；ExecuteAsync 抛 NotSupportedException。
/// </summary>
[Verb("Format", Noun = "Json", Aliases = ["fj", "json"])]
[Description("Formats items as JSON Lines (one JSON object per line).")]
public sealed class FormatJsonCommand : IPipelineSink<FormatJsonCommand.Args>
{
    /// <param name="Rows">最大行数。</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-r" })] int? Rows = null);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var formatter = new JsonFormatter();
        var spec = new ViewSpec
        {
            Columns = Array.Empty<ColumnSpec>(),
            Kind = ViewKind.Json,
            MaxRows = args.Rows,
        };
        await formatter.FormatAsync(input, spec, ctx.Host, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Format-Json is pipeline-only.");
}
