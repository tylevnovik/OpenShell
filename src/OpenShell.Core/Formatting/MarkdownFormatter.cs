using System.Globalization;
using System.Text;
using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// Markdown 表格渲染器。Per ADR-0011 §2 / §7. 输出 GitHub Flavored Markdown 表格：
/// <code>
/// | Name | Size | LastModified |
/// | --- | ---: | --- |
/// | file1.txt | 1024 | 2026-07-07 |
/// </code>
/// 列对齐启发式：纯数值列右对齐（<c>---:</c>），其余左对齐（<c>---</c>）。
/// 单元格值：<c>|</c> 转义为 <c>\|</c>，换行转义为 <c>&lt;br&gt;</c>；
/// null → 空串；bool → 小写 "true"/"false"；数值用 InvariantCulture。
/// 流式输出；MaxRows 达到后停止消费上游并发 truncated 提示。
/// </summary>
public sealed class MarkdownFormatter : IFormatter
{
    private const int SampleSize = 10;

    /// <summary>本渲染器支持的视图类型。</summary>
    public ViewKind SupportedKind => ViewKind.Markdown;

    /// <summary>
    /// 流式渲染 items 到 host 为 Markdown 表格。返回渲染的数据行数（不含表头/分隔行/footer）。
    /// </summary>
    public async ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken cancellationToken = default)
    {
        // 1. 缓冲前 SampleSize 行用于列发现与对齐启发式（与 TableFormatter 一致）。
        //    自动发现列时必须缓冲首项以读取 Properties.Keys；显式列也采样以推断对齐。
        var sample = new List<IItem>(SampleSize);
        await using var enumerator = items.GetAsyncEnumerator(cancellationToken);
        while (sample.Count < SampleSize && await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            sample.Add(enumerator.Current);
        }

        // 2. 解析列定义：显式优先，否则自动发现（首项 Properties + 标准字段 Name/Size/Modified）。
        IReadOnlyList<ColumnSpec> columns = spec.Columns.Count > 0
            ? spec.Columns
            : BuildAutoColumns(sample);

        // 3. 按采样数据推断每列对齐：全数值 → Right；其余 → Left。
        var alignments = ComputeAlignments(columns, sample);

        // 4. 表头 + 分隔行（即使无数据也输出，便于用户复制空表骨架）。
        if (spec.ShowHeader)
        {
            await host.WriteOutputLineAsync(HeaderRow(columns), cancellationToken).ConfigureAwait(false);
            await host.WriteOutputLineAsync(SeparatorRow(columns, alignments), cancellationToken).ConfigureAwait(false);
        }

        // 5. 输出已采样行。
        var emitted = 0;
        var truncated = false;
        foreach (var item in sample)
        {
            if (spec.MaxRows is { } max && emitted >= max)
            {
                truncated = true;
                break;
            }
            await host.WriteOutputLineAsync(DataRow(columns, item), cancellationToken).ConfigureAwait(false);
            emitted++;
        }

        // 6. 流式输出剩余行。
        if (!truncated)
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                if (spec.MaxRows is { } max && emitted >= max)
                {
                    truncated = true;
                    break;
                }
                await host.WriteOutputLineAsync(DataRow(columns, enumerator.Current), cancellationToken).ConfigureAwait(false);
                emitted++;
            }
        }

        // 7. footer（truncated 时显示截断行数）。
        if (spec.ShowFooter && truncated)
        {
            await host.WriteOutputLineAsync($"<!-- {emitted} item(s) (truncated) -->", cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    /// <summary>自动发现列：标准字段 Name/Size/Modified + 样本所有 Properties.Keys 的并集（按字典序）。</summary>
    private static IReadOnlyList<ColumnSpec> BuildAutoColumns(IReadOnlyList<IItem> sample)
    {
        // 无样本时退化为标准三列（与 CsvFormatter 行为一致）。
        if (sample.Count == 0)
        {
            return new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
                new ColumnSpec { Name = "Modified" },
            };
        }

        var columns = new List<ColumnSpec>
        {
            new() { Name = "Name" },
            new() { Name = "Size" },
            new() { Name = "Modified" },
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Size", "Modified" };
        foreach (var item in sample)
        {
            foreach (var key in item.Properties.Values.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(key))
                {
                    columns.Add(new ColumnSpec { Name = key });
                }
            }
        }
        return columns;
    }

    /// <summary>
    /// 计算每列对齐：列中所有非 null 值均为数值类型 → Right；否则 → Left。
    /// 数值类型：int/long/double/decimal 及其 unsigned 变体；bool/string/date 不算数值。
    /// </summary>
    private static Alignment[] ComputeAlignments(IReadOnlyList<ColumnSpec> columns, IReadOnlyList<IItem> sample)
    {
        var alignments = new Alignment[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            var colName = columns[c].Name;
            var allNumeric = true;
            var hasValue = false;
            foreach (var item in sample)
            {
                var value = ItemValueAccessor.GetValue(item, colName);
                if (value is null) continue;
                hasValue = true;
                if (!IsNumericType(value))
                {
                    allNumeric = false;
                    break;
                }
            }
            // 无值或非全数值 → Left（"unknown → left-align"）。
            alignments[c] = (hasValue && allNumeric) ? Alignment.Right : Alignment.Left;
        }
        return alignments;
    }

    /// <summary>判断值是否为数值类型（用于右对齐启发式）。bool 不算数值（按 task 规约视为 string-like）。</summary>
    private static bool IsNumericType(object value)
        => value is int or uint or long or ulong or short or ushort
            or byte or sbyte or float or double or decimal;

    /// <summary>表头行：<c>| Name | Size |</c>。</summary>
    private static string HeaderRow(IReadOnlyList<ColumnSpec> columns)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 0; c < columns.Count; c++)
        {
            var label = columns[c].DisplayLabel ?? columns[c].Name;
            sb.Append(' ').Append(EscapeCell(label)).Append(' ').Append('|');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 分隔行：<c>| --- | ---: | :---: |</c>。Left=<c>---</c>，Right=<c>---:</c>，Center=<c>:---:</c>。
    /// </summary>
    private static string SeparatorRow(IReadOnlyList<ColumnSpec> columns, Alignment[] alignments)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 0; c < columns.Count; c++)
        {
            var sep = alignments[c] switch
            {
                Alignment.Right => "---:",
                Alignment.Center => ":---:",
                _ => "---",
            };
            sb.Append(' ').Append(sep).Append(' ').Append('|');
        }
        return sb.ToString();
    }

    /// <summary>数据行：<c>| file1.txt | 1024 | 2026-07-07 |</c>。</summary>
    private static string DataRow(IReadOnlyList<ColumnSpec> columns, IItem item)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            var text = FormatCell(item, col);
            sb.Append(' ').Append(EscapeCell(text)).Append(' ').Append('|');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 格式化单元格值。null → 空串；bool → 小写 "true"/"false"；数值 → InvariantCulture（不带 N0 千分位）；
    /// DateTimeOffset/DateTime → 默认 "yyyy-MM-dd HH:mm"（locale 转 local time，InvariantCulture）；
    /// 显式 <paramref name="col.Format"/> + IFormattable → 用之；
    /// 其他 → <see cref="object.ToString"/>。
    /// </summary>
    private static string FormatCell(IItem item, ColumnSpec col)
    {
        var value = ItemValueAccessor.GetValue(item, col.Name);
        if (value is null) return string.Empty;
        // bool 必须小写（.ToString() 给 "True"/"False"）。
        if (value is bool b) return b ? "true" : "false";
        // 日期默认 "yyyy-MM-dd HH:mm"，转本地时间显示（与 ItemValueAccessor 一致）。
        if (value is DateTimeOffset dto)
        {
            return dto.ToLocalTime().ToString(col.Format ?? "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        if (value is DateTime dt)
        {
            return dt.ToString(col.Format ?? "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        // 显式 format + IFormattable：交给类型自身格式化（InvariantCulture 避免本地化）。
        if (col.Format is not null && value is IFormattable f)
        {
            return f.ToString(col.Format, CultureInfo.InvariantCulture);
        }
        // 数值默认用 InvariantCulture 的 plain ToString（不带 N0 千分位，匹配 task 示例 1024/2048）。
        if (value is IFormattable f2)
        {
            return f2.ToString(null, CultureInfo.InvariantCulture);
        }
        return value.ToString() ?? string.Empty;
    }

    /// <summary>Markdown 单元格转义：<c>|</c> → <c>\|</c>；CR/LF → <c>&lt;br&gt;</c>。</summary>
    private static string EscapeCell(string text)
    {
        if (text.Length == 0) return string.Empty;
        // 快速路径：无特殊字符直接返回。
        var needsEscape = false;
        foreach (var ch in text)
        {
            if (ch == '|' || ch == '\r' || ch == '\n')
            {
                needsEscape = true;
                break;
            }
        }
        if (!needsEscape) return text;

        var sb = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
        {
            if (ch == '|') sb.Append("\\|");
            else if (ch == '\r') continue; // CR 跳过，LF 写 <br>
            else if (ch == '\n') sb.Append("<br>");
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
