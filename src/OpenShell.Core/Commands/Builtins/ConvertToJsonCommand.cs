using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ConvertTo-Json</c> 命令：对象转 JSON 字符串。Per ADR-0048 §6.1.
/// <para>
/// 管道输入：<see cref="IItem"/> 流。buffering（必须全量才能序列化）。
/// 输出单个含 JSON 字符串的 <see cref="IItem"/>。
/// </para>
/// <para>
/// 使用 <see cref="System.Text.Json"/> 序列化，<c>-Depth</c> 控制嵌套深度（默认 2，与 PowerShell 5.1 一致），
/// <c>-Compress</c> 压缩输出（无缩进）。属性名 camelCase（per ADR-0022）。
/// </para>
/// </summary>
[Verb("ConvertTo", Noun = "Json", Aliases = ["cj"], PipelineOnly = true)]
[Description("Converts objects to JSON format.")]
public sealed class ConvertToJsonCommand : IPipelineTransform<ConvertToJsonCommand.Args>
{
    /// <summary>Arguments for <c>ConvertTo-Json</c>.</summary>
    /// <param name="Depth">序列化深度（默认 2，与 PowerShell 5.1 一致）。</param>
    /// <param name="Compress">压缩输出（无缩进）。</param>
    /// <param name="AsArray">即使单个输入也包装为数组（PS 6+ 行为）。</param>
    public record Args(
        [property: Parameter] int? Depth = null,
        [property: Parameter] bool Compress = false,
        [property: Parameter] bool AsArray = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var depth = args.Depth ?? 2;
        if (depth < 1) depth = 1;

        var items = new List<IItem>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            items.Add(item);
        }

        if (items.Count == 0 && !args.AsArray)
        {
            yield return MakeJsonItem("null");
            yield break;
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = !args.Compress,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Note: MaxDepth 不设置，用 System.Text.Json 默认值 64.
            // -Depth 参数控制的是 ItemToDict 的属性展开层级（PS 兼容语义），不是 JSON 序列化的递归深度限制。
        };

        // 单项且不强制数组 → 直接序列化该项。
        if (items.Count == 1 && !args.AsArray)
        {
            var json = SerializeItem(items[0], options, depth);
            yield return MakeJsonItem(json);
            yield break;
        }

        // 多项或 -AsArray → 序列化为 JSON 数组。
        var arrayJson = SerializeItems(items, options, depth);
        yield return MakeJsonItem(arrayJson);
    }

    /// <summary>不支持非管道调用。</summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ConvertTo-Json is pipeline-only, use it after |");

    private static string SerializeItem(IItem item, JsonSerializerOptions options, int depth)
    {
        var dict = ItemToDict(item, depth, currentDepth: 0);
        return JsonSerializer.Serialize(dict, options);
    }

    private static string SerializeItems(IReadOnlyList<IItem> items, JsonSerializerOptions options, int depth)
    {
        var list = items.Select(i => ItemToDict(i, depth, currentDepth: 0)).ToArray();
        return JsonSerializer.Serialize(list, options);
    }

    /// <summary>
    /// 把 <see cref="IItem"/> 转为字典，受 depth 限制。超过 depth 时属性值降级为 ToString()。
    /// </summary>
    private static Dictionary<string, object?> ItemToDict(IItem item, int maxDepth, int currentDepth)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = item.Name,
            ["path"] = item.Path.Display,
            ["kind"] = item.Kind.ToString(),
            ["size"] = item.Size,
        };

        foreach (var (key, value) in item.Properties.Values)
        {
            // Normalize property keys to camelCase (consistent with JsonSerializerOptions.PropertyNamingPolicy).
            // JsonNamingPolicy.CamelCase only affects CLR property names, not dictionary keys, so normalize manually.
            var camelKey = JsonNamingPolicy.CamelCase.ConvertName(key);
            dict[camelKey] = SimplifyValue(value, maxDepth, currentDepth);
        }

        return dict;
    }

    /// <summary>把 CLR 值简化为 JSON 友好的形式，受深度限制。</summary>
    private static object? SimplifyValue(object? value, int maxDepth, int currentDepth)
    {
        if (value is null) return null;
        if (currentDepth >= maxDepth)
            return value.ToString();

        return value switch
        {
            string s => s,
            int or long or double or float or decimal or bool => value,
            DateTimeOffset dto => dto.ToString("O"),
            DateTime dt => dt.ToString("O"),
            IItem nested => ItemToDict(nested, maxDepth, currentDepth + 1),
            System.Collections.IDictionary dict => DictToObj(dict, maxDepth, currentDepth + 1),
            System.Collections.IEnumerable enumerable => EnumerableToArray(enumerable, maxDepth, currentDepth + 1),
            _ => value.ToString(),
        };
    }

    private static Dictionary<string, object?> DictToObj(System.Collections.IDictionary dict, int maxDepth, int currentDepth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var key = entry.Key?.ToString() ?? "";
            result[key] = SimplifyValue(entry.Value, maxDepth, currentDepth);
        }
        return result;
    }

    private static List<object?> EnumerableToArray(System.Collections.IEnumerable enumerable, int maxDepth, int currentDepth)
    {
        var list = new List<object?>();
        foreach (var item in enumerable)
            list.Add(SimplifyValue(item, maxDepth, currentDepth));
        return list;
    }

    private static IItem MakeJsonItem(string json)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "json" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", json),
        };
}
