using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Compare-Object</c> 命令：与参照集对比，输出差异项。Per ADR-0010 §1.
/// <para>用法示例：<c>ls | compare "a.txt,b.txt,c.txt"</c></para>
/// <para>M2 简化：用 ReferenceSet 字符串（逗号分隔）作为参照集，按 <c>Name</c> 比较。</para>
/// <para>输出差异 Item：Properties 含 <c>SideIndicator</c>（<c>"&lt;="</c> 仅输入侧，<c>"=&gt;"</c> 仅参照侧）。</para>
/// <para>该 transform 是 buffering：必须缓存全部输入后才能计算差异。</para>
/// </summary>
[Verb("Compare", Noun = "Object", Aliases = ["compare", "diff"])]
[Description("Compares pipeline items to a reference set (comma-separated). Buffering transform.")]
public sealed class CompareObjectCommand : IPipelineTransform<CompareObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="ReferenceSet">逗号分隔的参照值字符串（按 Item.Name 比较）。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string ReferenceSet = "");

    /// <summary>变换上游流：缓存输入 → 与参照集对比 → 输出差异。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var referenceSet = new HashSet<string>(
            (args.ReferenceSet ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        var seenInInput = new HashSet<string>(StringComparer.Ordinal);

        // 1. 输出输入侧独有的项
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            seenInInput.Add(item.Name);
            if (!referenceSet.Contains(item.Name))
            {
                yield return new Item
                {
                    Path = item.Path,
                    Kind = item.Kind,
                    Size = item.Size,
                    Timestamps = item.Timestamps,
                    ContentType = item.ContentType,
                    Properties = item.Properties
                        .With("Name", item.Name)
                        .With("SideIndicator", "<="),
                };
            }
        }

        // 2. 输出参照集独有项（合成 Item）
        foreach (var refName in referenceSet)
        {
            if (seenInInput.Contains(refName))
                continue;
            yield return new Item
            {
                Path = new Paths.ItemPath { Provider = "memory", InternalPath = $"/ref/{Uri.EscapeDataString(refName)}" },
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty
                    .With("Name", refName)
                    .With("SideIndicator", "=>"),
            };
        }
    }

    /// <summary>
    /// 不支持直接调用：<c>Compare-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Compare-Object is pipeline-only, use it after |");
}
