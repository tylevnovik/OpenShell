using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ConvertTo-Csv</c> 命令：对象转 CSV 字符串。Per ADR-0048 §6.3.
/// <para>
/// 管道输入：<see cref="IItem"/> 流。buffering。
/// 输出：每行一个 <see cref="IItem"/>（含 Value 属性为 CSV 行字符串）。
/// </para>
/// <para>
/// <c>-NoTypeInformation</c> 默认 true（与 PS 6+ 一致），去掉首行 <c>#TYPE</c> 头。
/// <c>-Delimiter</c> 默认逗号。
/// </para>
/// </summary>
[Verb("ConvertTo", Noun = "Csv", Aliases = ["ccsv"], PipelineOnly = true)]
[Description("Converts objects to CSV format.")]
public sealed class ConvertToCsvCommand : IPipelineTransform<ConvertToCsvCommand.Args>
{
    /// <summary>Arguments for <c>ConvertTo-Csv</c>.</summary>
    /// <param name="NoTypeInformation">去掉首行 #TYPE 头（默认 true，与 PS 6+ 一致）。</param>
    /// <param name="Delimiter">分隔符（默认逗号）。</param>
    public record Args(
        [property: Parameter] bool NoTypeInformation = true,
        [property: Parameter] char? Delimiter = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var delimiter = args.Delimiter ?? ',';
        var items = new List<IItem>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            items.Add(item);
        }

        if (items.Count == 0)
            yield break;

        // 确定列：取第一个 item 的标准字段 + Properties.Keys。
        var columns = DiscoverColumns(items[0]);

        // #TYPE 行（PS 5.1 默认含，OpenShell 默认不含）。
        if (!args.NoTypeInformation)
        {
            var typeName = items[0].Kind.ToString();
            yield return MakeLineItem($"#TYPE {typeName}");
        }

        // 表头。
        yield return MakeLineItem(BuildHeader(columns, delimiter));

        // 数据行。
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MakeLineItem(BuildRow(item, columns, delimiter));
        }
    }

    /// <summary>不支持非管道调用。</summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ConvertTo-Csv is pipeline-only, use it after |");

    private static List<string> DiscoverColumns(IItem sample)
    {
        var columns = new List<string> { "Name", "Path", "Kind", "Size" };
        foreach (var key in sample.Properties.Values.Keys.Order(StringComparer.Ordinal))
        {
            if (!columns.Contains(key))
                columns.Add(key);
        }
        return columns;
    }

    private static string BuildHeader(List<string> columns, char delimiter)
        => string.Join(delimiter, columns.Select(EscapeField));

    private static string BuildRow(IItem item, List<string> columns, char delimiter)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(delimiter);
            var value = GetColumnValue(item, columns[i]);
            sb.Append(EscapeField(value ?? string.Empty));
        }
        return sb.ToString();
    }

    private static string? GetColumnValue(IItem item, string columnName)
        => columnName switch
        {
            "Name" => item.Name,
            "Path" => item.Path.Display,
            "Kind" => item.Kind.ToString(),
            "Size" => item.Size?.ToString() ?? "",
            _ => item.Properties[columnName]?.ToString() ?? "",
        };

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

    private static IItem MakeLineItem(string line)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "csv-line" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", line),
        };
}
