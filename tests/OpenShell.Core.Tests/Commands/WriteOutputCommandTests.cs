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
/// <c>Write-Output</c> unit tests. Per ADR-0048 §2.1.
/// Verifies that input objects are wrapped as <see cref="IItem"/> instances and
/// yielded to the success stream.
/// </summary>
public class WriteOutputCommandTests
{
    [Fact]
    public async Task Execute_NullInput_YieldsNothing()
    {
        var cmd = new WriteOutputCommand();
        var args = new WriteOutputCommand.Args(InputObject: null);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_EmptyArray_YieldsNothing()
    {
        var cmd = new WriteOutputCommand();
        var args = new WriteOutputCommand.Args(InputObject: Array.Empty<string>());
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_SingleValue_YieldsOneItem()
    {
        var cmd = new WriteOutputCommand();
        var args = new WriteOutputCommand.Args(InputObject: new[] { "hello" });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("hello");
    }

    [Fact]
    public async Task Execute_MultipleValues_YieldsOnePerValue()
    {
        var cmd = new WriteOutputCommand();
        var args = new WriteOutputCommand.Args(InputObject: new[] { "a", "b", "c" });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(3);
        results[0].Properties["Value"].Should().Be("a");
        results[1].Properties["Value"].Should().Be("b");
        results[2].Properties["Value"].Should().Be("c");
    }

    [Fact]
    public async Task Execute_NullValue_IsPreserved()
    {
        var cmd = new WriteOutputCommand();
        var args = new WriteOutputCommand.Args(InputObject: new string[] { null!, "x" });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Value"].Should().BeNull();
        results[1].Properties["Value"].Should().Be("x");
    }

    [Fact]
    public async Task Execute_YieldedItems_HavePropertyKind()
    {
        var cmd = new WriteOutputCommand();
        var args = new WriteOutputCommand.Args(InputObject: new[] { "v" });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Kind.Should().Be(ItemKind.Property);
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
