using System.Runtime.CompilerServices;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Runtime;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Sort-Object</c> 命令：按属性排序管道项。Per ADR-0010 §1 / ADR-0012 §8.
/// <para>用法示例：<c>ls | sort by size desc</c>、<c>ls | sort name</c></para>
/// <para>该 transform 是 buffering：必须缓存全部输入后再输出（Per ADR-0010 §6）。</para>
/// <para>M2 简化：仅支持单键排序。null 属性排在最后。</para>
/// </summary>
[Verb("Sort", Noun = "Object", Aliases = ["sort"])]
[Description("Sorts items by a property. Buffering transform.")]
public sealed class SortObjectCommand : IPipelineTransform<SortObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="PropertyScriptBlock">排序键脚本块（per ADR-0046）。$_ = item，返回值作为排序键。优先于 <paramref name="Property"/>。</param>
    /// <param name="Property">排序键属性名。null 时按 Name 排序。作为 ScriptBlock 缺省时的回退形式。</param>
    /// <param name="Descending">是否降序。默认升序。</param>
    /// <param name="Unique">是否仅保留唯一项（按排序键去重）。M2 简化：按 Name 去重。</param>
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock? PropertyScriptBlock = null,
        [property: Parameter(Position = 0)] string? Property = null,
        [property: Parameter] bool Descending = false,
        [property: Parameter] bool Unique = false);

    /// <summary>变换上游流：缓存全部输入 → 排序 → 输出。</summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new List<IItem>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            buffer.Add(item);
        }

        // 去重（按 Name 唯一）：ScriptBlock 与字符串路径共用。
        if (args.Unique)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<IItem>(buffer.Count);
            foreach (var item in buffer)
            {
                if (seen.Add(item.Name))
                    unique.Add(item);
            }
            buffer = unique;
        }

        // ScriptBlock 排序键（per ADR-0046 §5）：$_ = item，ScriptBlock 返回排序键，按预计算键排序。
        if (args.PropertyScriptBlock is not null)
        {
            var sb = args.PropertyScriptBlock;
            var sbCtx = sb.CapturedContext;
            var pairs = new List<(IItem Item, object? Key)>(buffer.Count);
            foreach (var item in buffer)
            {
                sbCtx.CurrentItem = item;
                sbCtx.CancellationToken = ct;
                pairs.Add((item, sb.Invoke(sbCtx)));
            }
            pairs.Sort((a, b) => CompareKeys(a.Key, b.Key, args.Descending));
            foreach (var (item, _) in pairs)
                yield return item;
            yield break;
        }

        var propName = args.Property ?? "name";
        var comparison = BuildComparison(propName, args.Descending);
        buffer.Sort(comparison);

        foreach (var item in buffer)
            yield return item;
    }

    private static int CompareKeys(object? left, object? right, bool descending)
    {
        // null 排在最后（无论升降序）
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var cmp = CompareValues(left, right);
        if (cmp is null) cmp = 0;
        return descending ? -cmp.Value : cmp.Value;
    }

    private static Comparison<IItem> BuildComparison(string property, bool descending)
    {
        return (a, b) =>
        {
            var av = ExprEvaluator.GetPropertyValue(property, a);
            var bv = ExprEvaluator.GetPropertyValue(property, b);

            // null 排在最后（无论升降序）
            if (av is null && bv is null) return 0;
            if (av is null) return 1;
            if (bv is null) return -1;

            var cmp = CompareValues(av, bv);
            if (cmp is null) cmp = 0;
            return descending ? -cmp.Value : cmp.Value;
        };
    }

    private static int? CompareValues(object? left, object? right)
    {
        if (left is null || right is null) return null;

        // 数值
        if (TryGetDouble(left, out var ld) && TryGetDouble(right, out var rd))
            return ld.CompareTo(rd);

        // 日期
        if (left is DateTimeOffset ldt && right is DateTimeOffset rdt)
            return ldt.CompareTo(rdt);
        if (left is DateTime ldt2 && right is DateTime rdt2)
            return ldt2.CompareTo(rdt2);

        // 字符串
        if (left is string ls && right is string rs)
            return string.Compare(ls, rs, StringComparison.OrdinalIgnoreCase);

        // 退化为字符串比较
        return string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
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
    /// 不支持直接调用：<c>Sort-Object</c> 是 pipeline-only。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Sort-Object is pipeline-only, use it after |");
}
