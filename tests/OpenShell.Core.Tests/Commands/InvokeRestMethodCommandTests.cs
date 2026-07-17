using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Invoke-RestMethod</c> unit tests. Per ADR-0048 §8.2.
/// 验证 HTTP 请求与响应按 Content-Type 自动解析（JSON / XML / 文本）。
/// 使用本地 HttpMessageHandler stub 避免真实网络 IO。
/// </summary>
public class InvokeRestMethodCommandTests
{
    [Fact]
    public async Task Execute_JsonResponse_ParsesToObjectItem()
    {
        var json = """{"name":"alice","age":30}""";
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["name"].Should().Be("alice");
        results[0].Properties["age"].Should().Be(30L);
    }

    [Fact]
    public async Task Execute_JsonArrayResponse_YieldsMultipleItems()
    {
        var json = """[1,2,3]""";
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(3);
        results[0].Properties["Value"].Should().Be(1L);
        results[1].Properties["Value"].Should().Be(2L);
        results[2].Properties["Value"].Should().Be(3L);
    }

    [Fact]
    public async Task Execute_TextResponse_YieldsStringItem()
    {
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("plain text response", Encoding.UTF8, "text/plain"),
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("plain text response");
    }

    [Fact]
    public async Task Execute_XmlResponse_YieldsXmlItem()
    {
        var xml = """<root><child>value</child></root>""";
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().NotBeNull();
        // Value 应包含 XML 文本（可能被 XDocument 重新序列化）。
        results[0].Properties["Value"]!.ToString()!.Should().Contain("root");
        results[0].Properties["Value"]!.ToString()!.Should().Contain("child");
    }

    [Fact]
    public async Task Execute_PostWithBody_SendsJson()
    {
        string? capturedBody = null;
        string? capturedContentType = null;
        var handler = new StubHandler((req, ct) =>
        {
            capturedBody = req.Content?.ReadAsStringAsync(ct).Result;
            capturedContentType = req.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            };
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(
            Uri: "http://test.local/x",
            Method: "POST",
            Body: """{"k":"v"}""");
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        capturedBody.Should().Be("""{"k":"v"}""");
        capturedContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Execute_CustomMethod_Parsed()
    {
        HttpMethod? captured = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req.Method;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(
            Uri: "http://test.local/x",
            Method: "PATCH");
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        captured.Should().Be(HttpMethod.Patch);
    }

    [Fact]
    public async Task Execute_ConnectionFailure_WritesNetworkError()
    {
        var handler = new StubHandler((req, ct) => throw new HttpRequestException("Connection refused"));
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.NetworkError);
        ctx.Errors!.LastError!.Operation.Should().Be("invoke-restmethod");
    }

    [Fact]
    public async Task Execute_500Status_ThrowsAndDoesNotYieldItems()
    {
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error body", Encoding.UTF8, "text/plain"),
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var act = async () =>
        {
            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);
        };

        // EnsureSuccessStatusCode 抛 HttpRequestException。
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Execute_CustomHeaders_PassedThrough()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });
        var cmd = new InvokeRestMethodCommand();
        var args = new InvokeRestMethodCommand.Args(
            Uri: "http://test.local/x",
            Headers: new Dictionary<string, string> { { "Authorization", "Bearer token123" } });
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        captured!.Headers.TryGetValues("Authorization", out var values).Should().BeTrue();
        values.Should().Contain("Bearer token123");
    }

    private static CommandContext TestCtx(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new ServiceBackedHost(client),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = new InMemoryErrorStream(),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request, cancellationToken));
    }

    private sealed class ServiceBackedHost : OpenShell.IHost
    {
        private readonly HttpClient _client;
        public ServiceBackedHost(HttpClient client) => _client = client;
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Parse("fs::/");
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new SingleServiceProvider(_client);
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly HttpClient _client;
        public SingleServiceProvider(HttpClient client) => _client = client;
        public object? GetService(Type serviceType) => serviceType == typeof(HttpClient) ? _client : null;
    }

    private sealed class EmptyObs<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) { observer.OnCompleted(); return new Disp(); }
    }

    private sealed class Disp : IDisposable { public void Dispose() { } }
}
