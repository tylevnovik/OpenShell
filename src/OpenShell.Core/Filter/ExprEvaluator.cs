using System.Collections;
using System.IO.Enumeration;
using System.Linq;
using OpenShell.Items;

namespace OpenShell.Filter;

/// <summary>
/// 表达式求值器。Per ADR-0012 §3.
/// 把 <see cref="ExprAst"/> 在 <see cref="IItem"/> 上下文下求值。
/// 类型不兼容的比较返回 false（不抛异常），便于容错（除非 <c>--strict</c>，M2 暂不实现 strict）。
/// </summary>
public sealed class ExprEvaluator
{
    /// <summary>对 AST 求值，返回 .NET 原生对象（bool/long/string/DateTimeOffset/null/...）。</summary>
    public object? Evaluate(ExprAst expr, IItem item)
    {
        return expr switch
        {
            ComparisonExpr c => EvaluateComparison(c, item),
            LogicalExpr l => EvaluateLogical(l, item),
            NotExpr n => !ToBool(Evaluate(n.Inner, item)),
            PropertyAccessExpr p => GetPropertyValue(p.Name, item),
            LiteralExpr lit => lit.Value,
            ProjectionExpr proj => Evaluate(proj.Expression, item),
            _ => throw new InvalidOperationException($"unknown expr {expr}"),
        };
    }

    private bool EvaluateComparison(ComparisonExpr c, IItem item)
    {
        var left = GetPropertyValue(c.Left.Name, item);
        var right = c.Right.Value;

        return c.Op switch
        {
            ComparisonOp.Eq => EqualsNullSafe(NormalizeForEquality(left), NormalizeForEquality(right)),
            ComparisonOp.Ne => !EqualsNullSafe(NormalizeForEquality(left), NormalizeForEquality(right)),
            ComparisonOp.Lt => CompareValues(left, right) is { } lt && lt < 0,
            ComparisonOp.Gt => CompareValues(left, right) is { } gt && gt > 0,
            ComparisonOp.Le => CompareValues(left, right) is { } le && le <= 0,
            ComparisonOp.Ge => CompareValues(left, right) is { } ge && ge >= 0,
            ComparisonOp.Glob => GlobMatch(left, right),
            ComparisonOp.NotGlob => !GlobMatch(left, right),
            ComparisonOp.In => InMatch(left, right),
            ComparisonOp.Contains => ContainsMatch(left, right),
            ComparisonOp.StartsWith => StartsWithMatch(left, right),
            ComparisonOp.EndsWith => EndsWithMatch(left, right),
            _ => false,
        };
    }

    private bool EvaluateLogical(LogicalExpr l, IItem item)
    {
        var leftBool = ToBool(Evaluate(l.Left, item));
        if (l.Op == LogicalOp.And && !leftBool) return false;
        if (l.Op == LogicalOp.Or && leftBool) return true;
        return ToBool(Evaluate(l.Right, item));
    }

    /// <summary>
    /// 取属性值。Per ADR-0012 §5.
    /// <para>内置字段：size/name/path/kind/modified/created/accessed/contenttype。</para>
    /// <para>其他从 <c>IItem.Properties</c> 字典查找，未找到返回 null。</para>
    /// </summary>
    public static object? GetPropertyValue(string name, IItem item)
    {
        if (string.IsNullOrEmpty(name)) return null;

        return name.ToLowerInvariant() switch
        {
            "size" => item.Size,
            "name" => item.Name,
            "path" => item.Path.Display,
            "kind" => item.Kind.ToString(),
            "modified" => item.Timestamps.Modified,
            "created" => item.Timestamps.Created,
            "accessed" => item.Timestamps.Accessed,
            "contenttype" => item.ContentType,
            _ => item.Properties[name],
        };
    }

    /// <summary>把任意值转为 bool（DSL 容错语义）。</summary>
    private static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        string s => !string.IsNullOrEmpty(s) && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static object? NormalizeForEquality(object? value)
    {
        // int → long 统一类型比较；float → double
        if (value is int i) return (long)i;
        if (value is float f) return (double)f;
        return value;
    }

    private static bool EqualsNullSafe(object? a, object? b)
    {
        // 两者皆 null → 相等；单边 null → 不等
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        // 数值类型宽松比较
        if (TryGetDouble(a, out var ad) && TryGetDouble(b, out var bd))
            return Math.Abs(ad - bd) < 1e-9;

        // DateTimeOffset 比较
        if (a is DateTimeOffset adt && b is DateTimeOffset bdt)
            return adt == bdt;

        return object.Equals(a, b);
    }

    /// <summary>比较两值，返回 -1/0/1。不可比较时返回 null。</summary>
    private static int? CompareValues(object? left, object? right)
    {
        if (left is null || right is null) return null;

        // 数值比较
        if (TryGetDouble(left, out var ld) && TryGetDouble(right, out var rd))
            return ld.CompareTo(rd);

        // 日期比较
        if (left is DateTimeOffset ldt && right is DateTimeOffset rdt)
            return ldt.CompareTo(rdt);
        if (left is DateTime ldt2 && right is DateTime rdt2)
            return ldt2.CompareTo(rdt2);

        // 字符串比较
        if (left is string ls && right is string rs)
            return string.Compare(ls, rs, StringComparison.Ordinal);

        // 退化为字符串比较（容错）
        if (left is not null && right is not null)
            return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);

        return null;
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        result = 0;
        if (value is null) return false;
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case decimal m: result = (double)m; return true;
            case string str when double.TryParse(str, out var parsed):
                result = parsed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Glob 匹配（*=任意、? = 单字符）。使用 FileSystemName.MatchesSimpleExpression。</summary>
    private static bool GlobMatch(object? left, object? right)
    {
        if (left is null) return false;
        var pattern = right?.ToString();
        if (string.IsNullOrEmpty(pattern)) return false;
        return FileSystemName.MatchesSimpleExpression(pattern, left.ToString(), ignoreCase: true);
    }

    /// <summary>in 操作：检查 left 是否在 right（数组）中。</summary>
    private static bool InMatch(object? left, object? right)
    {
        if (right is null) return false;
        var elements = right as object[] ?? ToArray(right);
        foreach (var elem in elements)
        {
            if (EqualsNullSafe(NormalizeForEquality(left), NormalizeForEquality(elem)))
                return true;
        }
        return false;
    }

    private static object[] ToArray(object value)
    {
        if (value is object[] arr) return arr;
        if (value is IEnumerable enumerable and not string)
            return enumerable.Cast<object?>().Where(o => o is not null).Select(o => o!).ToArray();
        return new[] { value };
    }

    /// <summary>contains：字符串包含 或 集合包含。</summary>
    private static bool ContainsMatch(object? left, object? right)
    {
        if (left is null || right is null) return false;
        var rightStr = right.ToString() ?? "";

        // 集合 contains
        if (left is IEnumerable enumerable and not string)
        {
            foreach (var elem in enumerable)
            {
                if (EqualsNullSafe(NormalizeForEquality(elem), NormalizeForEquality(right)))
                    return true;
            }
            return false;
        }

        // 字符串 contains
        var leftStr = left.ToString() ?? "";
        return leftStr.Contains(rightStr);
    }

    private static bool StartsWithMatch(object? left, object? right)
    {
        if (left is null || right is null) return false;
        var leftStr = left.ToString() ?? "";
        var rightStr = right.ToString() ?? "";
        return leftStr.StartsWith(rightStr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWithMatch(object? left, object? right)
    {
        if (left is null || right is null) return false;
        var leftStr = left.ToString() ?? "";
        var rightStr = right.ToString() ?? "";
        return leftStr.EndsWith(rightStr, StringComparison.OrdinalIgnoreCase);
    }
}
