using System.Text;
using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// ASCII 表格渲染器。Per ADR-0011 §5. 流式渲染：
/// 1. 缓冲前 10 行采样估算列宽（min(max(content_max, header_len), 40)）
/// 2. 输出边框 + 表头 + 边框
/// 3. 输出已采样的行 + 流式输出剩余行（锁定列宽）
/// 4. 输出边框 + footer（"N item(s)"）
/// MaxRows 达到后停止消费上游并发 truncated 提示。
/// </summary>
public sealed class TableFormatter : IFormatter
{
    private const int SampleSize = 10;
    private const int MaxColumnWidth = 40;

    public ViewKind SupportedKind => ViewKind.Table;

    public async ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken cancellationToken = default)
    {
        // 1. 缓冲前 SampleSize 行用于采样列宽与列发现。
        // enumerator 由 await using 在方法返回时自动 Dispose（触发上游取消）。
        var sample = new List<IItem>(SampleSize);
        await using var enumerator = items.GetAsyncEnumerator(cancellationToken);
        while (sample.Count < SampleSize && await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            sample.Add(enumerator.Current);
        }

        // 2. 解析列定义：空则自动发现。
        var columns = spec.Columns.Count > 0
            ? spec.Columns
            : ItemValueAccessor.AutoDiscoverColumns(sample);

        // 3. 计算列宽。
        var widths = ComputeColumnWidths(columns, sample);

        // 4. 输出表头 + 边框。
        var emitted = 0;
        var truncated = false;

        if (spec.ShowHeader)
        {
            await host.WriteOutputLineAsync(BorderLine(widths), cancellationToken).ConfigureAwait(false);
            await host.WriteOutputLineAsync(HeaderLine(columns, widths), cancellationToken).ConfigureAwait(false);
            await host.WriteOutputLineAsync(BorderLine(widths), cancellationToken).ConfigureAwait(false);
        }

        // 5. 输出已采样行。
        foreach (var item in sample)
        {
            if (spec.MaxRows is { } max && emitted >= max)
            {
                truncated = true;
                break;
            }
            await host.WriteOutputLineAsync(RowLine(columns, widths, item), cancellationToken).ConfigureAwait(false);
            emitted++;
        }

        // 6. 流式输出剩余行（MaxRows 未达到时）。
        if (!truncated)
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                if (spec.MaxRows is { } max && emitted >= max)
                {
                    truncated = true;
                    break;
                }
                await host.WriteOutputLineAsync(RowLine(columns, widths, enumerator.Current), cancellationToken).ConfigureAwait(false);
                emitted++;
            }
        }

        // 7. footer + 末尾边框。
        if (spec.ShowFooter)
        {
            if (spec.ShowHeader)
            {
                await host.WriteOutputLineAsync(BorderLine(widths), cancellationToken).ConfigureAwait(false);
            }
            var footer = truncated
                ? $"  {emitted} item(s) (truncated)"
                : $"  {emitted} item(s)";
            await host.WriteOutputLineAsync(footer, cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    /// <summary>列宽：min(max(content_max, header_len), MaxColumnWidth)。显式 Width 优先。</summary>
    private static int[] ComputeColumnWidths(IReadOnlyList<ColumnSpec> columns, IReadOnlyList<IItem> sample)
    {
        var widths = new int[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            if (col.Width is { } explicitWidth)
            {
                widths[c] = explicitWidth;
                continue;
            }

            var header = col.DisplayLabel ?? col.Name;
            var max = header.Length;
            foreach (var item in sample)
            {
                var text = ItemValueAccessor.GetFormatted(item, col.Name, col.Format);
                if (text.Length > max) max = text.Length;
            }
            widths[c] = Math.Min(max, MaxColumnWidth);
        }
        return widths;
    }

    private static string BorderLine(int[] widths)
    {
        var sb = new StringBuilder();
        sb.Append('+');
        foreach (var w in widths)
        {
            sb.Append(new string('-', w + 2));
            sb.Append('+');
        }
        return sb.ToString();
    }

    private static string HeaderLine(IReadOnlyList<ColumnSpec> columns, int[] widths)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 0; c < columns.Count; c++)
        {
            var header = columns[c].DisplayLabel ?? columns[c].Name;
            sb.Append(' ');
            sb.Append(AlignText(header, widths[c], columns[c].Align));
            sb.Append(' ');
            sb.Append('|');
        }
        return sb.ToString();
    }

    private static string RowLine(IReadOnlyList<ColumnSpec> columns, int[] widths, IItem item)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            var text = ItemValueAccessor.GetFormatted(item, col.Name, col.Format);
            sb.Append(' ');
            sb.Append(AlignText(text, widths[c], col.Align));
            sb.Append(' ');
            sb.Append('|');
        }
        return sb.ToString();
    }

    /// <summary>对齐文本到指定宽度。超长截断；不足补空格。</summary>
    private static string AlignText(string text, int width, Alignment align)
    {
        if (text.Length > width)
        {
            // 截断并加省略号（若宽度 >= 3）。
            return width >= 3 ? text[..(width - 3)] + "..." : text[..width];
        }

        return align switch
        {
            Alignment.Right => text.PadLeft(width),
            Alignment.Center => CenterPad(text, width),
            _ => text.PadRight(width),
        };
    }

    private static string CenterPad(string text, int width)
    {
        var totalPad = width - text.Length;
        if (totalPad <= 0) return text;
        var left = totalPad / 2;
        var right = totalPad - left;
        return new string(' ', left) + text + new string(' ', right);
    }
}
