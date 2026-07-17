using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Invoke-RestMethod</c> 命令：HTTP 请求并自动解析响应。Per ADR-0048 §8.2.
/// <para>
/// 响应 <c>Content-Type</c> 决定解析：
/// <c>application/json</c> → JSON 对象 / 数组；<c>application/xml</c> / <c>text/xml</c> → XDocument；
/// <c>text/plain</c> → 字符串；其他 → byte[]。
/// </para>
/// </summary>
[Verb("Invoke", Noun = "RestMethod", Aliases = ["irm"])]
[Description("Sends an HTTP request and parses the response.")]
public sealed class InvokeRestMethodCommand : ICommand<InvokeRestMethodCommand.Args>
{
    /// <summary>Arguments for <c>Invoke-RestMethod</c>.</summary>
    /// <param name="Uri">请求 URI（mandatory）。</param>
    /// <param name="Method">HTTP 方法（默认 Get）。</param>
    /// <param name="Headers">请求头（字典）。</param>
    /// <param name="Body">请求体。</param>
    /// <param name="ContentType">Content-Type 头（默认 application/json）。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Uri,
        [property: Parameter] string? Method = null,
        [property: Parameter] Dictionary<string, string>? Headers = null,
        [property: Parameter] string? Body = null,
        [property: Parameter] string? ContentType = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = ResolveHttpClient(ctx);

        using var request = new HttpRequestMessage(
            ParseMethod(args.Method), args.Uri);

        if (args.Headers is not null)
        {
            foreach (var (key, value) in args.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (!string.IsNullOrEmpty(args.Body))
        {
            var bodyContentType = args.ContentType ?? "application/json";
            var content = new StringContent(args.Body!, Encoding.UTF8, bodyContentType);
            request.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NetworkError,
                Message = $"HTTP request to '{args.Uri}' failed: {ex.Message}",
                Operation = "invoke-restmethod",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        using var resp = response;
        resp.EnsureSuccessStatusCode();

        var contentStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var responseContentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";

        // 根据 Content-Type 解析。
        foreach (var item in ParseResponse(contentStr, responseContentType, ct))
            yield return item;

        await Task.CompletedTask;
    }

    private static HttpClient ResolveHttpClient(CommandContext ctx)
    {
        var client = ctx.Host.Services.GetService(typeof(HttpClient)) as HttpClient;
        return client ?? new HttpClient();
    }

    private static HttpMethod ParseMethod(string? method)
        => (method?.ToUpperInvariant()) switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            "PATCH" => HttpMethod.Patch,
            _ => HttpMethod.Get,
        };

    private static IEnumerable<IItem> ParseResponse(string content, string contentType, CancellationToken ct)
    {
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // JSON 解析。
            using var doc = JsonDocument.Parse(content);
            foreach (var item in ConvertJsonElement(doc.RootElement))
                yield return item;
        }
        else if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            // XML 解析 → XDocument。
            var doc = XDocument.Parse(content);
            yield return MakeValueItem(doc.Root?.Name?.ToString() ?? "xml", doc.ToString());
        }
        else
        {
            // 纯文本。
            yield return MakeValueItem("text", content);
        }
    }

    private static IEnumerable<IItem> ConvertJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var props = PropertyBag.Empty;
                foreach (var prop in element.EnumerateObject())
                    props = props.With(prop.Name, GetJsonValue(prop.Value));
                yield return new Item
                {
                    Path = new ItemPath { Provider = "fs", InternalPath = "object" },
                    Kind = ItemKind.Property,
                    Properties = props,
                };
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    foreach (var item in ConvertJsonElement(child))
                        yield return item;
                break;
            case JsonValueKind.String:
                yield return MakeValueItem("value", element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                    yield return MakeValueItem("value", l);
                else
                    yield return MakeValueItem("value", element.GetDouble());
                break;
            case JsonValueKind.True:
                yield return MakeValueItem("value", true);
                break;
            case JsonValueKind.False:
                yield return MakeValueItem("value", false);
                break;
            case JsonValueKind.Null:
                yield return MakeValueItem("value", null);
                break;
        }
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString(),
        };
    }

    private static IItem MakeValueItem(string name, object? value)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = name },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", value),
        };
}
