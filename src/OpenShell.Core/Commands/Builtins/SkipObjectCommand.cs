using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Skip-Object</c> 命令：跳过前 N 项。Per ADR-0010 §1.
/// <para>用法示例：<c>ls | skip 5</c></para>
/// </summary>
[Verb("Skip", Noun = "Object", Aliases = ["skip"])]
[Description("Skips the first N items from the pipeline.")]
public sealed class SkipObjectCommand : IPipelineTransform<SkipObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="Count">要跳过的项数。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] int Count = 0);

    /// <summary>变换上游流：跳过前 N 项后输出剩余项。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = args.Count;
        var skipped = 0;
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (skipped < count)
            {
                skipped++;
                continue;
            }
            yield return item;
        }
    }

    /// <summary>
    /// 不支持直接调用：<c>Skip-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Skip-Object is pipeline-only, use it after |");
}
