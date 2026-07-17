using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Out-Host</c> 命令：把上游 IItem 流直接交给 Host 默认渲染器（Host.WriteItemsAsync）。
/// 相当于 Out-Default，但走 Host 自身实现（CLI 用内置 Renderer，GUI 用 GridFormatter 视图模型）。
/// </summary>
[Verb("Out", Noun = "Host", Aliases = ["oh", "host"])]
[Description("Sends items to the host's default renderer.")]
public sealed class OutHostCommand : IPipelineSink<OutHostCommand.Args>
{
    public record Args;

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        await ctx.Host.WriteItemsAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Out-Host is pipeline-only.");
}
