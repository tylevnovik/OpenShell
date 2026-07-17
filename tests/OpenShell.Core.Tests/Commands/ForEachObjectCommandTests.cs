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
/// <c>ForEach-Object</c> unit tests. Per ADR-0048 §1.1.
/// Verifies pipeline transform behaviour: passthrough, -MemberName projection,
/// and -ProcessCommand string-method invocation.
/// </summary>
public class ForEachObjectCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args();
        var ctx = TestCtx();

        var act = async () =>
        {
            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);
        };

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Transform_NoArgs_PassesThroughUnchanged()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            Make("alpha", value: "first"),
            Make("beta", value: "second"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("alpha");
        results[1].Name.Should().Be("beta");
    }

    [Fact]
    public async Task Transform_MemberName_ExtractsProperty()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args(MemberName: "Value");
        var ctx = TestCtx();
        var input = Items(
            Make("a.txt", value: "AAA"),
            Make("b.txt", value: "BBB"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Value"].Should().Be("AAA");
        results[1].Properties["Value"].Should().Be("BBB");
    }

    [Fact]
    public async Task Transform_MemberName_Name_ReturnsItemName()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args(MemberName: "Name");
        var ctx = TestCtx();
        var input = Items(Make("first.txt"), Make("second.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Name"].Should().Be("first.txt");
        results[1].Properties["Name"].Should().Be("second.txt");
    }

    [Fact]
    public async Task Transform_ProcessCommand_ToUpper()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args(ProcessCommand: "ToUpper");
        var ctx = TestCtx();
        var input = Items(
            Make("hello"),
            Make("world"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Value"].Should().Be("HELLO");
        results[1].Properties["Value"].Should().Be("WORLD");
    }

    [Fact]
    public async Task Transform_ProcessCommand_ToLower()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args(ProcessCommand: "ToLower");
        var ctx = TestCtx();
        var input = Items(Make("MIXED"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("mixed");
    }

    [Fact]
    public async Task Transform_ProcessCommand_Trim_StripsWhitespace()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args(ProcessCommand: "Trim");
        var ctx = TestCtx();
        var input = Items(Make("  padded  "));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("padded");
    }

    [Fact]
    public async Task Transform_EmptyInput_YieldsNothing()
    {
        var cmd = new ForEachObjectCommand();
        var args = new ForEachObjectCommand.Args(MemberName: "Value");
        var ctx = TestCtx();
        var input = Items();

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    private static IItem Make(string name, string? value = null)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = name },
            Kind = ItemKind.File,
            Properties = value is null
                ? PropertyBag.Empty
                : PropertyBag.Empty.With("Value", value),
        };

    private static async IAsyncEnumerable<IItem> Items(params IItem[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
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
