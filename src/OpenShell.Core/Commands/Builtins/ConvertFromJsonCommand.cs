using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ConvertFrom-Json</c> 命令：JSON 字符串解析为对象。Per ADR-0048 §6.2.
/// <para>
/// 输入：JSON 字符串（通过 <c>-InputObject</c> 或管道）。
/// 输出：解析后的 <see cref="IItem"/>（PSCustomObject 风格）或基本类型项。
/// </para>
/// <para>
/// 使用 <see cref="JsonDocument"/> 解析。<c>-AsHashtable</c> 返回字典风格项（属性名即 key）。
/// JSON 对象 → Property 项；JSON 数组 → 多项输出；基本类型 → Property 项含 Value 属性。
/// </para>
/// </summary>
[Verb("ConvertFrom", Noun = "Json", Aliases = ["cfj"])]
[Description("Converts a JSON string to objects.")]
public sealed class ConvertFromJsonCommand : IPipelineTransform<ConvertFromJsonCommand.Args>
{
    /// <summary>Arguments for <c>ConvertFrom-Json</c>.</summary>
    /// <param name="InputObject">JSON 字符串（管道或位置绑定）。</param>
    /// <param name="AsHashtable">返回 hashtable 风格项（默认 PSCustomObject 风格）。</param>
    public record Args(
        [property: Parameter(Position = 0)] string? InputObject = null,
        [property: Parameter] bool AsHashtable = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 收集所有输入的字符串值（管道输入的 IItem.Name 即字符串值）。
        var jsonStrings = new List<string>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            // 管道输入：优先取 Properties["Value"]，其次取 Name。
            var value = item.Properties["Value"]?.ToString() ?? item.Name;
            if (!string.IsNullOrWhiteSpace(value))
                jsonStrings.Add(value);
        }

        // 也支持 -InputObject 直接绑定。
        if (jsonStrings.Count == 0 && !string.IsNullOrWhiteSpace(args.InputObject))
            jsonStrings.Add(args.InputObject);

        foreach (var jsonStr in jsonStrings)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(jsonStr);
            foreach (var result in ConvertElement(doc.RootElement, args.AsHashtable, ct))
                yield return result;
        }
    }

    /// <summary>直接 Execute：通过 -InputObject 绑定。</summary>
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.InputObject))
            yield break;

        using var doc = JsonDocument.Parse(args.InputObject!);
        foreach (var result in ConvertElement(doc.RootElement, args.AsHashtable, ct))
            yield return result;

        await Task.CompletedTask;
    }

    private static IEnumerable<IItem> ConvertElement(JsonElement element, bool asHashtable, CancellationToken ct)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                yield return ConvertObject(element, asHashtable);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var item in ConvertElement(child, asHashtable, ct))
                        yield return item;
                }
                break;
            case JsonValueKind.String:
                yield return MakeValueItem(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                    yield return MakeValueItem(l);
                else
                    yield return MakeValueItem(element.GetDouble());
                break;
            case JsonValueKind.True:
                yield return MakeValueItem(true);
                break;
            case JsonValueKind.False:
                yield return MakeValueItem(false);
                break;
            case JsonValueKind.Null:
                yield return MakeValueItem(null);
                break;
        }
    }

    private static IItem ConvertObject(JsonElement objElement, bool asHashtable)
    {
        var props = PropertyBag.Empty;
        foreach (var prop in objElement.EnumerateObject())
        {
            props = props.With(prop.Name, GetElementValue(prop.Value));
        }
        return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "object" },
            Kind = ItemKind.Property,
            Properties = props,
        };
    }

    private static object? GetElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => ConvertObjectToDict(element),
            JsonValueKind.Array => element.EnumerateArray().Select(GetElementValue).ToArray(),
            _ => element.ToString(),
        };
    }

    private static Dictionary<string, object?> ConvertObjectToDict(JsonElement objElement)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in objElement.EnumerateObject())
            dict[prop.Name] = GetElementValue(prop.Value);
        return dict;
    }

    private static IItem MakeValueItem(object? value)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "value" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", value),
        };
}
