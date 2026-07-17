using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Take-Object</c> 命令：取前 N 项。Per ADR-0010 §1.
/// <para>用法示例：<c>ls | take 10</c></para>
/// <para>别名：<c>head</c>、<c>first</c>。</para>
/// </summary>
[Verb("Take", Noun = "Object", Aliases = ["take", "head", "first"])]
[Description("Takes the first N items from the pipeline.")]
public sealed class TakeObjectCommand : IPipelineTransform<TakeObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="Count">要取的项数。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] int Count = 0);

    /// <summary>变换上游流：只输出前 N 项。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = args.Count;
        if (count <= 0)
            yield break;

        var emitted = 0;
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (emitted >= count) yield break;
            yield return item;
            emitted++;
        }
    }

    /// <summary>
    /// 不支持直接调用：<c>Take-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Take-Object is pipeline-only, use it after |");
}
