using System.Text.Json;
using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// JSON Lines 渲染器。Per ADR-0011 §2. 每行一个 JSON 对象，便于 jq 解析。
/// 字段：path / name / kind / size / modified / created / accessed / properties。
/// 流式输出，无缓冲；MaxRows 达到后停止消费上游并发 truncated 提示（最后一行）。
/// </summary>
public sealed class JsonFormatter : IFormatter
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ViewKind SupportedKind => ViewKind.Json;

    public async ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken cancellationToken = default)
    {
        var emitted = 0;
        var truncated = false;

        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (spec.MaxRows is { } max && emitted >= max)
            {
                truncated = true;
                break;
            }

            var line = SerializeItem(item);
            await host.WriteOutputLineAsync(line, cancellationToken).ConfigureAwait(false);
            emitted++;
        }

        if (spec.ShowFooter && truncated)
        {
            await host.WriteOutputLineAsync($"// {emitted} item(s) (truncated)", cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    private static string SerializeItem(IItem item)
    {
        // 显式构造字典而非依赖反射，确保字段顺序稳定 + properties 子对象展开。
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = item.Path.Display,
            ["name"] = item.Name,
            ["kind"] = item.Kind.ToString(),
            ["size"] = item.Size,
            ["modified"] = item.Timestamps.Modified?.UtcDateTime.ToString("O"),
            ["created"] = item.Timestamps.Created?.UtcDateTime.ToString("O"),
            ["accessed"] = item.Timestamps.Accessed?.UtcDateTime.ToString("O"),
            ["properties"] = item.Properties.Values,
        };

        return JsonSerializer.Serialize(dict, s_options);
    }
}
