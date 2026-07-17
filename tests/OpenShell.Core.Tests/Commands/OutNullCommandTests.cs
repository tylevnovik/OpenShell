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
/// <c>Out-Null</c> unit tests. Per ADR-0048 §2.6.
/// Verifies that the sink consumes all upstream items and emits nothing to the host
/// (its purpose is to discard pipeline output while still triggering side effects).
/// </summary>
public class OutNullCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new OutNullCommand();
        var args = new OutNullCommand.Args();
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
    public async Task Consume_EmptyInput_CompletesWithoutOutput()
    {
        var host = new CapturingHost();
        var cmd = new OutNullCommand();
        var args = new OutNullCommand.Args();
        var ctx = TestCtx(host);
        var input = ToStream(Array.Empty<IItem>());

        await cmd.Consume(input, args, ctx, default);

        host.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Consume_MultipleItems_DiscardsAllAndEmitsNothing()
    {
        var host = new CapturingHost();
        var cmd = new OutNullCommand();
        var args = new OutNullCommand.Args();
        var ctx = TestCtx(host);
        var input = ToStream(MakeItems("a.txt", "b.txt", "c.txt"));

        await cmd.Consume(input, args, ctx, default);

        host.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Consume_PropagatesCancellation()
    {
        var host = new CapturingHost();
        var cmd = new OutNullCommand();
        var args = new OutNullCommand.Args();
        var ctx = TestCtx(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var input = ToStream(MakeItems("never.txt"), cts.Token);

        var act = async () => await cmd.Consume(input, args, ctx, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Consume_SingleItem_DoesNotEmitToHost()
    {
        var host = new CapturingHost();
        var cmd = new OutNullCommand();
        var args = new OutNullCommand.Args();
        var ctx = TestCtx(host);
        var input = ToStream(MakeItems("ignored.txt"));

        await cmd.Consume(input, args, ctx, default);

        host.Lines.Should().BeEmpty();
    }

    private static IItem[] MakeItems(params string[] names)
        => names.Select(n => (IItem)Item.File(ItemPath.Parse($"fs::/tmp/{n}"))).ToArray();

    private static async IAsyncEnumerable<IItem> ToStream(
        IEnumerable<IItem> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }

    private static CommandContext TestCtx(CapturingHost? host = null)
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host ?? new CapturingHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = new InMemoryErrorStream(),
        };
    }

    private sealed class CapturingHost : OpenShell.IHost
    {
        private readonly ItemPath _location = ItemPath.Parse("fs::/");
        public List<string> Lines { get; } = new();

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get => _location; set { } }
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new EmptyServiceProvider();

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }

        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
