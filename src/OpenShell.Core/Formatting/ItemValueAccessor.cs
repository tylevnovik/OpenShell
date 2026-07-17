using System.Globalization;
using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// Item 字段访问与格式化辅助。统一从 IItem 提取列值，并按 ColumnSpec.Format 渲染为字符串。
/// 标准字段（Name/Size/Modified/Created/Accessed/Path/Kind）走 IItem 属性；
/// 其他字段走 Properties 字典索引。Size 默认按 N0 字节显示；Modified 默认 "yyyy-MM-dd HH:mm"。
/// </summary>
internal static class ItemValueAccessor
{
    /// <summary>从 IItem 提取指定列名对应的原始值。</summary>
    public static object? GetValue(IItem item, string columnName)
        => columnName switch
        {
            "Name" => item.Name,
            "Size" => item.Size,
            "Modified" => item.Timestamps.Modified,
            "Created" => item.Timestamps.Created,
            "Accessed" => item.Timestamps.Accessed,
            "Path" => item.Path.Display,
            "Kind" => item.Kind,
            _ => item.Properties[columnName],
        };

    /// <summary>格式化值为字符串。null → 空串；DateTimeOffset 按 locale 转换。</summary>
    public static string FormatValue(object? value, string? format)
    {
        if (value is null) return string.Empty;

        // DateTimeOffset：默认 "yyyy-MM-dd HH:mm"，转 local time 显示。
        if (value is DateTimeOffset dto)
        {
            return dto.ToLocalTime().ToString(format ?? "yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }
        if (value is DateTime dt)
        {
            return dt.ToString(format ?? "yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        // 显式格式 + IFormattable：交给类型自身格式化。
        if (format is not null && value is IFormattable f)
        {
            return f.ToString(format, CultureInfo.CurrentCulture);
        }

        // long 默认按 N0（如 Size 字节）。
        if (value is long l)
        {
            return l.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (value is int i)
        {
            return i.ToString("N0", CultureInfo.CurrentCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    /// <summary>按列名提取并格式化值。</summary>
    public static string GetFormatted(IItem item, string columnName, string? format)
        => FormatValue(GetValue(item, columnName), format);

    /// <summary>自动发现列：取样本中所有 Properties.Keys 的并集，前置标准字段 Name/Size/Modified。</summary>
    public static IReadOnlyList<ColumnSpec> AutoDiscoverColumns(IEnumerable<IItem> sample)
    {
        var columns = new List<ColumnSpec>
        {
            new() { Name = "Name" },
            new() { Name = "Size", Align = Alignment.Right },
            new() { Name = "Modified" },
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Size", "Modified" };
        foreach (var item in sample)
        {
            foreach (var key in item.Properties.Values.Keys)
            {
                if (seen.Add(key))
                {
                    columns.Add(new ColumnSpec { Name = key });
                }
            }
        }

        return columns;
    }

    /// <summary>Out-Default 默认列：IItem 标准 5 字段（Name/Kind/Size/Modified/Path）。Per ADR-0011 §7.</summary>
    public static IReadOnlyList<ColumnSpec> StandardColumns() => new[]
    {
        new ColumnSpec { Name = "Name" },
        new ColumnSpec { Name = "Kind" },
        new ColumnSpec { Name = "Size", Align = Alignment.Right },
        new ColumnSpec { Name = "Modified" },
        new ColumnSpec { Name = "Path" },
    };
}
