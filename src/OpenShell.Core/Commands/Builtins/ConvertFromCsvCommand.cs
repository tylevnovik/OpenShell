using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ConvertFrom-Csv</c> 命令：CSV 字符串转对象（PSCustomObject 风格）。Per ADR-0048 §6.4.
/// <para>
/// 管道输入：CSV 字符串（每行一个 <see cref="IItem"/>，Value 或 Name 为 CSV 行）。
/// 输出：每个数据行一个 <see cref="IItem"/>，属性名为列名。
/// </para>
/// <para>
/// 第一行视为表头（除非 <c>-Header</c> 指定）。<c>-Delimiter</c> 默认逗号。
/// </para>
/// </summary>
[Verb("ConvertFrom", Noun = "Csv", Aliases = ["cfcsv"], PipelineOnly = true)]
[Description("Converts CSV strings to objects.")]
public sealed class ConvertFromCsvCommand : IPipelineTransform<ConvertFromCsvCommand.Args>
{
    /// <summary>Arguments for <c>ConvertFrom-Csv</c>.</summary>
    /// <param name="Header">自定义列名（无表头时）。</param>
    /// <param name="Delimiter">分隔符（默认逗号）。</param>
    public record Args(
        [property: Parameter] string[]? Header = null,
        [property: Parameter] char? Delimiter = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var delimiter = args.Delimiter ?? ',';
        var lines = new List<string>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var value = item.Properties["Value"]?.ToString() ?? item.Name;
            if (!string.IsNullOrEmpty(value))
                lines.Add(value);
        }

        if (lines.Count == 0)
            yield break;

        // 跳过开头的 #TYPE 行（PS 5.1 兼容）。#TYPE 行在表头之前。
        int lineIdx = 0;
        while (lineIdx < lines.Count && lines[lineIdx].StartsWith("#TYPE ", StringComparison.OrdinalIgnoreCase))
            lineIdx++;

        // 确定表头。
        List<string> headers;
        int dataStart;
        if (args.Header is { Length: > 0 } customHeaders)
        {
            headers = customHeaders.ToList();
            dataStart = lineIdx;
        }
        else
        {
            if (lineIdx >= lines.Count) yield break;
            headers = ParseCsvLine(lines[lineIdx], delimiter);
            dataStart = lineIdx + 1;
        }

        for (int i = dataStart; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var fields = ParseCsvLine(lines[i], delimiter);
            yield return MakeObjectItem(headers, fields);
        }
    }

    /// <summary>不支持非管道调用。</summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ConvertFrom-Csv is pipeline-only, use it after |");

    /// <summary>解析一行 CSV，处理引号转义。</summary>
    internal static List<string> ParseCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    // 双引号转义。
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                sb.Append(ch);
                i++;
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                    i++;
                    continue;
                }
                if (ch == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                    i++;
                    continue;
                }
                sb.Append(ch);
                i++;
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static IItem MakeObjectItem(List<string> headers, List<string> fields)
    {
        var props = PropertyBag.Empty;
        for (int i = 0; i < headers.Count && i < fields.Count; i++)
        {
            props = props.With(headers[i], fields[i]);
        }
        return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "csv-object" },
            Kind = ItemKind.Property,
            Properties = props,
        };
    }
}
