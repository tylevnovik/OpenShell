using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Out-Null</c> 命令：消费上游流但不输出（丢弃）。常用于触发 Source 的副作用或性能测量。
/// </summary>
[Verb("Out", Noun = "Null", Aliases = ["on", "null"])]
[Description("Discards all items from the pipeline.")]
public sealed class OutNullCommand : IPipelineSink<OutNullCommand.Args>
{
    public record Args;

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        // 全消费但不输出。透传 CancellationToken 以支持上游取消。
        await foreach (var _ in input.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // 显式空循环体，表示丢弃每项。
        }
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Out-Null is pipeline-only.");
}
