using System.Runtime.CompilerServices;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Join-Path</c> unit tests. Per ADR-0048 §3.4.
/// Verifies path segment joining with canonical '/' separator, additional child paths,
/// and error handling for missing mandatory parameters.
/// </summary>
public class JoinPathCommandTests
{
    [Fact]
    public async Task Execute_TwoSegments_JoinedWithSlash()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(Path: "C:/Users", ChildPath: "blmpt");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("C:/Users/blmpt");
    }

    [Fact]
    public async Task Execute_AdditionalChildPath_AppendsAllSegments()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(
            Path: "C:/",
            ChildPath: "Users",
            AdditionalChildPath: new[] { "blmpt", "Documents", "file.txt" });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("C:/Users/blmpt/Documents/file.txt");
    }

    [Fact]
    public async Task Execute_BackslashNormalisedToSlash()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(Path: "C:\\Users", ChildPath: "blmpt");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be("C:/Users/blmpt");
    }

    [Fact]
    public async Task Execute_TrailingSeparatorStripped()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(Path: "C:/Users/", ChildPath: "/blmpt");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be("C:/Users/blmpt");
    }

    [Fact]
    public async Task Execute_MissingPath_WritesError()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(Path: null, ChildPath: "blmpt");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors.Should().NotBeNull();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Message.Should().Contain("Path");
    }

    [Fact]
    public async Task Execute_MissingChildPath_WritesError()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(Path: "C:/Users", ChildPath: null);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError!.Message.Should().Contain("ChildPath");
    }

    [Fact]
    public async Task Execute_AbsoluteChildPath_ReplacesParent()
    {
        var cmd = new JoinPathCommand();
        var args = new JoinPathCommand.Args(Path: "C:/Users", ChildPath: "D:/Other");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be("D:/Other");
    }

    private static CommandContext TestCtx()
    {
        var errors = new InMemoryErrorStream();
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = errors,
        };
    }

    private sealed class NopHost : OpenShell.IHost
    {
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Parse("fs::/");
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new EmptyServiceProvider();
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyObs<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) { observer.OnCompleted(); return new Disp(); }
    }

    private sealed class Disp : IDisposable { public void Dispose() { } }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
