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
/// <c>Resolve-Path</c> unit tests. Per ADR-0048 §3.2.
/// Verifies relative path resolution against the current location, absolute path
/// pass-through, and -Relative output.
/// </summary>
public class ResolvePathCommandTests
{
    [Fact]
    public async Task Execute_RelativePath_ResolvedAgainstCurrentLocation()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "subdir/file.txt" });
        var ctx = TestCtx(ItemPath.Parse("fs::C:/Users/blmpt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Path.InternalPath.Should().Be("C:/Users/blmpt/subdir/file.txt");
    }

    [Fact]
    public async Task Execute_AbsolutePath_PassedThrough()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "C:/Windows/system32" });
        var ctx = TestCtx(ItemPath.Parse("fs::C:/Users/blmpt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Path.InternalPath.Should().Be("C:/Windows/system32");
    }

    [Fact]
    public async Task Execute_DisplayProperty_ReturnsFullPath()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "subdir" });
        var ctx = TestCtx(ItemPath.Parse("fs::C:/Users"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Display"].Should().Be("fs::C:/Users/subdir");
    }

    [Fact]
    public async Task Execute_Relative_ReturnsRelativePath()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "C:/Users/blmpt/file.txt" }, Relative: true);
        var ctx = TestCtx(ItemPath.Parse("fs::C:/Users"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["ProviderPath"].Should().Be("blmpt/file.txt");
    }

    [Fact]
    public async Task Execute_RelativeSameDirectory_ReturnsDot()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "C:/Users" }, Relative: true);
        var ctx = TestCtx(ItemPath.Parse("fs::C:/Users"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["ProviderPath"].Should().Be(".");
    }

    [Fact]
    public async Task Execute_MultiplePaths_YieldsOnePerInput()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "a.txt", "b.txt" });
        var ctx = TestCtx(ItemPath.Parse("fs::C:/dir"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Path.InternalPath.Should().Be("C:/dir/a.txt");
        results[1].Path.InternalPath.Should().Be("C:/dir/b.txt");
    }

    [Fact]
    public async Task Execute_EmptyPath_WritesError()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: new[] { "  " });
        var ctx = TestCtx(ItemPath.Parse("fs::C:/dir"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_NullPath_YieldsNothing()
    {
        var cmd = new ResolvePathCommand();
        var args = new ResolvePathCommand.Args(Path: null);
        var ctx = TestCtx(ItemPath.Parse("fs::C:/dir"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    private static CommandContext TestCtx(ItemPath location)
    {
        var errors = new InMemoryErrorStream();
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new NopHost(location),
            CurrentLocation = location,
            Errors = errors,
        };
    }

    private sealed class NopHost : OpenShell.IHost
    {
        private readonly ItemPath _location;
        public NopHost(ItemPath location) => _location = location;
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get => _location; set { } }
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
