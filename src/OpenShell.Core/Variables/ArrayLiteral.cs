namespace OpenShell.Variables;

/// <summary>
/// 数组字面量运行时辅助。Per ADR-0047 §7.1.
/// 把元素列表打包为 object[]; null 元素按 PowerShell 语义过滤掉 (除非显式 $null 标记).
/// 解析器集成 (逗号操作符 / @() 子表达式) 延后到 ADR-0045.
/// </summary>
public static class ArrayLiteral
{
    /// <summary>
    /// 创建 object[], 过滤掉 null 元素。
    /// </summary>
    /// <param name="values">元素列表 (允许 null, 会被过滤)。</param>
    /// <returns>非 null 元素组成的 object[]。</returns>
    public static object[] Create(params object?[] values)
        => values.Where(v => v is not null).Select(v => v!).ToArray();

    /// <summary>
    /// 创建 object[], 过滤掉 null 元素。
    /// </summary>
    /// <param name="values">元素序列 (允许 null, 会被过滤)。</param>
    /// <returns>非 null 元素组成的 object[]。</returns>
    public static object[] CreateRange(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Where(v => v is not null).Select(v => v!).ToArray();
    }

    /// <summary>创建空数组 (Length == 0)。</summary>
    public static object[] Empty() => Array.Empty<object>();
}
