using System.Text;
using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// CSV 渲染器。Per ADR-0011 §2. 标准 CSV：第一行表头，后续每行一个 Item。
/// 字段顺序：Name, Size, Modified（标准字段）+ Properties.Keys（按字典序）。
/// 含逗号/引号/换行的字段自动加引号转义。流式输出。
/// </summary>
public sealed class CsvFormatter : IFormatter
{
    public ViewKind SupportedKind => ViewKind.Csv;

    public async ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken cancellationToken = default)
    {
        // 流式输出，但表头需要先确定。表头分两种情况：
        // 1. 用户显式指定 Columns → 直接用之。
        // 2. 自动发现 → 缓冲首个 Item，取其 Properties.Keys 作为表头。
        IReadOnlyList<ColumnSpec> columns;
        IAsyncEnumerator<IItem>? enumerator = null;
        IItem? firstItem = null;

        if (spec.Columns.Count > 0)
        {
            columns = spec.Columns;
        }
        else
        {
            // 缓冲第一个 Item，发现 Properties.Keys。
            enumerator = items.GetAsyncEnumerator(cancellationToken);
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                // 空流：仅输出标准字段表头（无 Properties）。
                columns = new[]
                {
                    new ColumnSpec { Name = "Name" },
                    new ColumnSpec { Name = "Size" },
                    new ColumnSpec { Name = "Modified" },
                };
            }
            else
            {
                firstItem = enumerator.Current;
                columns = BuildAutoColumns(firstItem);
            }
        }

        // 表头。
        if (spec.ShowHeader)
        {
            var headerSb = new StringBuilder();
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) headerSb.Append(',');
                headerSb.Append(EscapeField(columns[i].DisplayLabel ?? columns[i].Name));
            }
            await host.WriteOutputLineAsync(headerSb.ToString(), cancellationToken).ConfigureAwait(false);
        }

        var emitted = 0;
        var truncated = false;

        // 输出第一个 Item（若已缓冲）。
        if (firstItem is not null)
        {
            await host.WriteOutputLineAsync(BuildRow(columns, firstItem), cancellationToken).ConfigureAwait(false);
            emitted++;
        }

        // 流式输出剩余。
        // 若已通过 enumerator 缓冲首项，则继续迭代之；否则用 await foreach。
        if (enumerator is not null)
        {
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    if (spec.MaxRows is { } max && emitted >= max)
                    {
                        truncated = true;
                        break;
                    }
                    await host.WriteOutputLineAsync(BuildRow(columns, enumerator.Current), cancellationToken).ConfigureAwait(false);
                    emitted++;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (spec.MaxRows is { } max && emitted >= max)
                {
                    truncated = true;
                    break;
                }
                await host.WriteOutputLineAsync(BuildRow(columns, item), cancellationToken).ConfigureAwait(false);
                emitted++;
            }
        }

        if (spec.ShowFooter && truncated)
        {
            await host.WriteOutputLineAsync($"# {emitted} item(s) (truncated)", cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    private static IReadOnlyList<ColumnSpec> BuildAutoColumns(IItem sample)
    {
        var columns = new List<ColumnSpec>
        {
            new() { Name = "Name" },
            new() { Name = "Size" },
            new() { Name = "Modified" },
        };
        foreach (var key in sample.Properties.Values.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            columns.Add(new ColumnSpec { Name = key });
        }
        return columns;
    }

    private static string BuildRow(IReadOnlyList<ColumnSpec> columns, IItem item)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var col = columns[i];
            var text = ItemValueAccessor.GetFormatted(item, col.Name, col.Format);
            sb.Append(EscapeField(text));
        }
        return sb.ToString();
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行/CR → 包裹引号，内部引号双写。</summary>
    private static string EscapeField(string field)
    {
        if (field.Length == 0) return string.Empty;
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return field;

        var sb = new StringBuilder(field.Length + 2);
        sb.Append('"');
        foreach (var ch in field)
        {
            if (ch == '"') sb.Append("\"\"");
            else sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
