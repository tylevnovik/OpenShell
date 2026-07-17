using System.Runtime.CompilerServices;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Runtime;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Select-Object</c> 命令：投影 / 截取管道项。Per ADR-0010 §1 / ADR-0012 §8.
/// <para>用法示例：</para>
/// <para>  <c>ls | select name, size</c>：保留指定属性</para>
/// <para>  <c>ls | select -First 10</c>：取前 10 项（等价 take）</para>
/// <para>  <c>ls | select -Skip 5 -First 10</c>：跳过 5 项后取 10 项</para>
/// <para>M2 简化：构造新 Item，Properties 仅含指定属性；First/Last/Skip 合并到此命令。</para>
/// </summary>
[Verb("Select", Noun = "Object", Aliases = ["select", "project"])]
[Description("Projects items onto selected properties, or takes/skips items.")]
public sealed class SelectObjectCommand : IPipelineTransform<SelectObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="PropertyScriptBlock">投影脚本块（per ADR-0046）。$_ = item，返回值作为选中值输出。优先于 <paramref name="Properties"/>。</param>
    /// <param name="Properties">要保留的属性名列表（逗号分隔）。null 时不裁剪 Properties。作为 ScriptBlock 缺省时的回退形式。</param>
    /// <param name="First">取前 N 项。</param>
    /// <param name="Last">取末尾 N 项。</param>
    /// <param name="Skip">跳过前 N 项。</param>
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock? PropertyScriptBlock = null,
        [property: Parameter(Position = 0)] string[]? Properties = null,
        [property: Parameter] int? First = null,
        [property: Parameter] int? Last = null,
        [property: Parameter] int? Skip = null);

    /// <summary>变换上游流。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var properties = args.Properties;
        var first = args.First;
        var last = args.Last;
        var skip = args.Skip ?? 0;
        var sb = args.PropertyScriptBlock;

        // 投影：ScriptBlock 优先（per ADR-0046 §5，$_ = item 计算选中值），否则按属性名列表回退。
        IItem Project(IItem item)
        {
            if (sb is { } block)
            {
                var blockCtx = block.CapturedContext;
                blockCtx.CurrentItem = item;
                blockCtx.CancellationToken = ct;
                var value = block.Invoke(blockCtx);
                return new Item
                {
                    Path = item.Path,
                    Kind = ItemKind.Property,
                    Properties = PropertyBag.Empty.With("Value", value),
                };
            }
            return ProjectItem(item, properties);
        }

        // 流式分支：无 -Last 时直接流式处理（-First + -Skip）。
        if (last is null)
        {
            var emitted = 0;
            var taken = 0;
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (emitted < skip)
                {
                    emitted++;
                    continue;
                }
                if (first is { } f && taken >= f)
                    yield break;
                yield return Project(item);
                taken++;
            }
            yield break;
        }

        // buffering 分支：-Last 需要全量缓存。
        var buffer = new List<IItem>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            buffer.Add(item);
        }

        var skipCount = Math.Min(skip, buffer.Count);
        var remaining = buffer.Skip(skipCount).ToList();
        if (first is { } firstN)
            remaining = remaining.Take(firstN).ToList();
        if (last is { } lastN)
            remaining = remaining.TakeLast(lastN).ToList();

        foreach (var item in remaining)
            yield return Project(item);
    }

    /// <summary>构造新 Item：Properties 仅包含指定属性。</summary>
    private static IItem ProjectItem(IItem item, string[]? properties)
    {
        if (properties is null || properties.Length == 0)
            return item;

        var bag = PropertyBag.Empty;
        foreach (var name in properties)
        {
            var value = ExprEvaluator.GetPropertyValue(name, item);
            bag = bag.With(name, value);
        }

        if (item is Item concrete)
            return concrete with { Properties = bag };
        // 退化：包装为 Item（保留原 Path/Kind/Size/Timestamps）
        return new Item
        {
            Path = item.Path,
            Kind = item.Kind,
            Size = item.Size,
            Timestamps = item.Timestamps,
            ContentType = item.ContentType,
            Properties = bag,
        };
    }

    /// <summary>
    /// 不支持直接调用：<c>Select-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Select-Object is pipeline-only, use it after |");
}
