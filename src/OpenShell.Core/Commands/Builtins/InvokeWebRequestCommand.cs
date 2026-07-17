using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Invoke-WebRequest</c> 命令：HTTP 请求。Per ADR-0048 §8.1.
/// <para>
/// 输出 <see cref="IItem"/> 含 <c>StatusCode</c> / <c>Headers</c> / <c>Content</c> / <c>RawContent</c> 属性。
/// <c>-UseBasicParsing</c> 始终启用（OpenShell 不含 IE 引擎）。
/// </para>
/// <para>
/// <see cref="HttpClient"/> 从 <see cref="IHost.Services"/> 解析（per Program.cs DI 注册）。
/// </para>
/// </summary>
[Verb("Invoke", Noun = "WebRequest", Aliases = ["iwr", "curl", "wget"])]
[Description("Sends an HTTP request and returns the response.")]
public sealed class InvokeWebRequestCommand : ICommand<InvokeWebRequestCommand.Args>
{
    /// <summary>Arguments for <c>Invoke-WebRequest</c>.</summary>
    /// <param name="Uri">请求 URI（mandatory）。</param>
    /// <param name="Method">HTTP 方法（默认 Get）。</param>
    /// <param name="Headers">请求头（字典）。</param>
    /// <param name="Body">请求体（字符串或字节）。</param>
    /// <param name="ContentType">Content-Type 头。</param>
    /// <param name="TimeoutSec">超时秒数。</param>
    /// <param name="UseBasicParsing">兼容参数（始终启用）。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Uri,
        [property: Parameter] string? Method = null,
        [property: Parameter] Dictionary<string, string>? Headers = null,
        [property: Parameter] string? Body = null,
        [property: Parameter] string? ContentType = null,
        [property: Parameter] int? TimeoutSec = null,
        [property: Parameter] bool UseBasicParsing = true);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = ResolveHttpClient(ctx);

        using var request = new HttpRequestMessage(
            ParseMethod(args.Method), args.Uri);

        // 请求头。
        if (args.Headers is not null)
        {
            foreach (var (key, value) in args.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        // 请求体。
        if (!string.IsNullOrEmpty(args.Body))
        {
            var content = new StringContent(args.Body!, Encoding.UTF8);
            if (!string.IsNullOrEmpty(args.ContentType))
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(args.ContentType!);
            request.Content = content;
        }

        // 超时。
        if (args.TimeoutSec is { } timeout)
            client.Timeout = TimeSpan.FromSeconds(timeout);

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
                Operation = "invoke-webrequest",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        using var resp = response;
        var contentStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var headersDict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in resp.Headers)
        {
            headersDict[header.Key] = string.Join(", ", header.Value);
        }
        foreach (var header in resp.Content.Headers)
        {
            headersDict[header.Key] = string.Join(", ", header.Value);
        }

        var rawContent = $"HTTP/{resp.Version}\r\n{(int)resp.StatusCode} {resp.StatusCode}\r\n";
        foreach (var header in headersDict)
            rawContent += $"{header.Key}: {header.Value}\r\n";
        rawContent += "\r\n" + contentStr;

        var props = PropertyBag.Empty
            .With("StatusCode", (int)resp.StatusCode)
            .With("StatusDescription", resp.StatusCode.ToString())
            .With("Headers", headersDict)
            .With("Content", contentStr)
            .With("RawContent", rawContent)
            .With("RawContentLength", (long)contentStr.Length);

        yield return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = args.Uri },
            Kind = ItemKind.Property,
            Properties = props,
        };

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
            "TRACE" => HttpMethod.Trace,
            _ => HttpMethod.Get,
        };
}
