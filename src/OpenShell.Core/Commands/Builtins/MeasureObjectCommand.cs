using System.Runtime.CompilerServices;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Measure-Object</c> 命令：统计管道项。Per ADR-0010 §1.
/// <para>用法示例：</para>
/// <para>  <c>ls | measure</c>：输出 Count=总项数</para>
/// <para>  <c>ls | measure size</c>：输出 Count/Sum/Average/Min/Max（对 size 数值字段）</para>
/// <para>该 transform 是 buffering：必须缓存全部输入后才能聚合输出。</para>
/// <para>输出单个 Item：Properties 含 Count/Sum/Average/Min/Max。</para>
/// </summary>
[Verb("Measure", Noun = "Object", Aliases = ["measure", "count"])]
[Description("Measures pipeline items: count and numeric aggregates.")]
public sealed class MeasureObjectCommand : IPipelineTransform<MeasureObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="Property">要聚合的数值属性名。null 时只输出 Count。</param>
    public record Args(
        [property: Parameter] string? Property = null);

    /// <summary>变换上游流：缓存全部输入 → 聚合 → 输出单个 Item。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var property = args.Property;
        long count = 0;
        double? sum = null;
        double? min = null;
        double? max = null;

        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            count++;
            if (property is null) continue;
            var v = ExprEvaluator.GetPropertyValue(property, item);
            if (TryGetDouble(v, out var dv))
            {
                sum = (sum ?? 0) + dv;
                min = min is null ? dv : Math.Min(min.Value, dv);
                max = max is null ? dv : Math.Max(max.Value, dv);
            }
        }

        var bag = PropertyBag.Empty
            .With("Count", count);
        if (property is not null)
        {
            bag = bag
                .With("Sum", sum)
                .With("Average", sum is not null && count > 0 ? sum / count : (double?)null)
                .With("Min", min)
                .With("Max", max)
                .With("Property", property);
        }

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "memory", InternalPath = "/measure" },
            Kind = ItemKind.Property,
            Properties = bag,
        };
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        result = 0;
        if (value is null) return false;
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case decimal m: result = (double)m; return true;
            default: return false;
        }
    }

    /// <summary>
    /// 不支持直接调用：<c>Measure-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Measure-Object is pipeline-only, use it after |");
}
