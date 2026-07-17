using System.Text;
using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// 列表渲染器。Per ADR-0011 §2. 每个 Item 一段，每行 <c>PropertyName: Value</c>，
/// 段间空行分隔。流式输出，无缓冲。MaxRows 达到后停止消费上游并发 truncated 提示。
/// </summary>
public sealed class ListFormatter : IFormatter
{
    public ViewKind SupportedKind => ViewKind.List;

    public async ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken cancellationToken = default)
    {
        var emitted = 0;
        var first = true;
        var truncated = false;

        // 解析列定义：空则用标准字段（Name/Kind/Size/Modified/Created/Accessed/Path）+ Properties.Keys。
        // 由于流式无法预知所有 Properties.Keys，使用第一个 Item 的 Properties 作为参考；
        // 后续 Item 的额外 key 在遇到时动态加入（仅在显式 Properties=null 时）。
        var columns = spec.Columns;
        List<string>? dynamicKeys = null;

        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (spec.MaxRows is { } max && emitted >= max)
            {
                truncated = true;
                break;
            }

            if (!first)
            {
                await host.WriteOutputLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            }
            first = false;

            // 当前 Item 使用的列：显式指定则用之，否则用标准字段 + 当前 Item 的 Properties.Keys。
            IReadOnlyList<ColumnSpec> effectiveColumns;
            if (columns.Count > 0)
            {
                effectiveColumns = columns;
            }
            else
            {
                dynamicKeys ??= new List<string> { "Name", "Kind", "Size", "Modified", "Created", "Accessed", "Path" };
                var current = new List<ColumnSpec>();
                foreach (var k in dynamicKeys)
                {
                    current.Add(new ColumnSpec { Name = k });
                }
                // 当前 Item 的 Properties 中未列入的 key 追加。
                foreach (var key in item.Properties.Values.Keys)
                {
                    if (!dynamicKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        dynamicKeys.Add(key);
                        current.Add(new ColumnSpec { Name = key });
                    }
                }
                effectiveColumns = current;
            }

            foreach (var col in effectiveColumns)
            {
                var label = col.DisplayLabel ?? col.Name;
                var value = ItemValueAccessor.GetFormatted(item, col.Name, col.Format);
                var sb = new StringBuilder();
                sb.Append("  ");
                sb.Append(label);
                sb.Append(": ");
                sb.Append(value);
                await host.WriteOutputLineAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
            }

            emitted++;
        }

        if (spec.ShowFooter)
        {
            var footer = truncated
                ? $"  {emitted} item(s) (truncated)"
                : $"  {emitted} item(s)";
            await host.WriteOutputLineAsync(footer, cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }
}
