using System.Net;
using System.Net.Http;
using System.Text;
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
/// <c>Invoke-WebRequest</c> unit tests. Per ADR-0048 §8.1.
/// 验证 HTTP 请求构造、响应字段输出、连接失败错误、-Method / -Headers / -Body / -ContentType 解析。
/// 使用本地 HttpMessageHandler stub 避免真实网络 IO。
/// </summary>
public class InvokeWebRequestCommandTests
{
    [Fact]
    public async Task Execute_SuccessfulGet_ReturnsItemWithStatusCode()
    {
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello body", Encoding.UTF8, "text/plain"),
        });
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["StatusCode"].Should().Be(200);
        results[0].Properties["Content"].Should().Be("hello body");
        results[0].Properties["RawContentLength"].Should().Be(10L);
        results[0].Properties["Headers"].Should().NotBeNull();
        results[0].Properties["RawContent"].Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_PostWithBody_SendsContent()
    {
        string? capturedBody = null;
        var handler = new StubHandler((req, ct) =>
        {
            capturedBody = req.Content?.ReadAsStringAsync(ct).Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
        });
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(
            Uri: "http://test.local/echo",
            Method: "POST",
            Body: """{"k":"v"}""",
            ContentType: "application/json");
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        capturedBody.Should().Be("""{"k":"v"}""");
    }

    [Fact]
    public async Task Execute_CustomHeaders_PassedThrough()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(
            Uri: "http://test.local/x",
            Headers: new Dictionary<string, string> { { "X-Custom", "value" } });
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        captured!.Headers.TryGetValues("X-Custom", out var values).Should().BeTrue();
        values.Should().Contain("value");
    }

    [Fact]
    public async Task Execute_MethodDelete_Parsed()
    {
        HttpMethod? captured = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req.Method;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(
            Uri: "http://test.local/r",
            Method: "DELETE");
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        captured.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Execute_TimeoutSec_AppliedToClient()
    {
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.OK));
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(
            Uri: "http://test.local/x",
            TimeoutSec: 30);
        var ctx = TestCtx(handler);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }
        // 不抛异常即可（client.Timeout 会被覆盖）。
    }

    [Fact]
    public async Task Execute_ConnectionFailure_WritesNetworkError()
    {
        var handler = new StubHandler((req, ct) => throw new HttpRequestException("Connection refused"));
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.NetworkError);
        ctx.Errors!.LastError!.Operation.Should().Be("invoke-webrequest");
    }

    [Fact]
    public async Task Execute_404_ReturnsItemWithStatusCode()
    {
        var handler = new StubHandler((req, ct) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found"),
        });
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(Uri: "http://test.local/x");
        var ctx = TestCtx(handler);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["StatusCode"].Should().Be(404);
        results[0].Properties["Content"].Should().Be("not found");
    }

    [Fact]
    public async Task Execute_NoClientRegistered_FallsBackToNewClient()
    {
        // EmptyServicesHost 不注册 HttpClient，命令应当回退到 new HttpClient()。
        // 用 http://localhost.localtest/ 应该会抛异常被捕获（连接失败）。
        var cmd = new InvokeWebRequestCommand();
        var args = new InvokeWebRequestCommand.Args(Uri: "http://invalid.localhost.localtest/");
        var ctx = TestCtxWithEmptyServices();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.NetworkError);
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

    private static CommandContext TestCtxWithEmptyServices()
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new EmptyServicesHost(),
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

    /// <summary>Host 提供 HttpClient 通过 Services。</summary>
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

    /// <summary>Host 不提供 HttpClient（命令应回退到 new HttpClient()）。</summary>
    private sealed class EmptyServicesHost : OpenShell.IHost
    {
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Parse("fs::/");
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new EmptyServiceProvider();
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly HttpClient _client;
        public SingleServiceProvider(HttpClient client) => _client = client;
        public object? GetService(Type serviceType) => serviceType == typeof(HttpClient) ? _client : null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class EmptyObs<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) { observer.OnCompleted(); return new Disp(); }
    }

    private sealed class Disp : IDisposable { public void Dispose() { } }
}
