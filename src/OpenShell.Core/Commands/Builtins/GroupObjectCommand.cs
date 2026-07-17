using System.Runtime.CompilerServices;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Group-Object</c> 命令：按属性分组。Per ADR-0010 §1 / §6.
/// <para>用法示例：<c>ls | group kind</c>、<c>get-process | group company</c></para>
/// <para>该 transform 是 buffering：必须缓存全部输入后才能分组输出。</para>
/// <para>每组输出一个 Item：Properties 含 GroupKey（分组键）和 Count（计数）。</para>
/// </summary>
[Verb("Group", Noun = "Object", Aliases = ["group", "group-by"])]
[Description("Groups items by a property. Buffering transform.")]
public sealed class GroupObjectCommand : IPipelineTransform<GroupObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="Property">分组键属性名。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Property = "");

    /// <summary>变换上游流：缓存全部输入 → 按 Property 分组 → 每组输出一个 Item。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var property = args.Property ?? "";

        // 保留插入顺序的分组：key → items 列表
        var groups = new Dictionary<string, List<IItem>>(StringComparer.Ordinal);
        var order = new List<string>();

        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var key = ExprEvaluator.GetPropertyValue(property, item)?.ToString() ?? "<null>";
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<IItem>();
                groups[key] = bucket;
                order.Add(key);
            }
            bucket.Add(item);
        }

        foreach (var key in order)
        {
            var bucket = groups[key];
            yield return new Item
            {
                Path = new Paths.ItemPath { Provider = "memory", InternalPath = $"/group/{Uri.EscapeDataString(key)}" },
                Kind = ItemKind.Container,
                Properties = PropertyBag.Empty
                    .With("GroupKey", key)
                    .With("Count", (long)bucket.Count)
                    .With("Group", bucket),
            };
        }
    }

    /// <summary>
    /// 不支持直接调用：<c>Group-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Group-Object is pipeline-only, use it after |");
}
