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
/// <c>Out-Default</c> unit tests. Per ADR-0048 §2.7 / ADR-0011 §7.
/// Verifies that the default sink renders upstream items via <c>TableFormatter</c>
/// using the standard 5 columns (Name / Kind / Size / Modified / Path) and writes
/// the rendered rows to the host UI.
/// </summary>
public class OutDefaultCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new OutDefaultCommand();
        var args = new OutDefaultCommand.Args();
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
    public async Task Consume_Items_RendersNameColumnToHost()
    {
        var host = new CapturingHost();
        var cmd = new OutDefaultCommand();
        var args = new OutDefaultCommand.Args();
        var ctx = TestCtx(host);
        var input = ToStream(MakeItems("foo.txt", "bar.txt"));

        await cmd.Consume(input, args, ctx, default);

        // The table formatter emits header + rows + footer; at minimum each name must appear.
        host.Lines.Should().Contain(l => l.Contains("foo.txt"));
        host.Lines.Should().Contain(l => l.Contains("bar.txt"));
    }

    [Fact]
    public async Task Consume_EmptyInput_StillWritesHeader()
    {
        var host = new CapturingHost();
        var cmd = new OutDefaultCommand();
        var args = new OutDefaultCommand.Args();
        var ctx = TestCtx(host);
        var input = ToStream(Array.Empty<IItem>());

        await cmd.Consume(input, args, ctx, default);

        // Even with no rows, the formatter writes the header line.
        host.Lines.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Consume_SingleItem_RendersSizeAndName()
    {
        var host = new CapturingHost();
        var cmd = new OutDefaultCommand();
        var args = new OutDefaultCommand.Args();
        var ctx = TestCtx(host);
        var input = ToStream(new[]
        {
            Item.File(ItemPath.Parse("fs::/tmp/big.bin"), size: 4096),
        });

        await cmd.Consume(input, args, ctx, default);

        // TableFormatter uses the current culture's thousands separator for numeric
        // columns. We only assert the name and the digit portion of the size value
        // to stay culture-agnostic.
        host.Lines.Should().Contain(l => l.Contains("big.bin"));
        host.Lines.Should().Contain(l => l.Contains("4") && l.Contains("096"));
    }

    [Fact]
    public async Task Consume_PropagatesCancellation()
    {
        var host = new CapturingHost();
        var cmd = new OutDefaultCommand();
        var args = new OutDefaultCommand.Args();
        var ctx = TestCtx(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var input = ToStream(MakeItems("never.txt"), cts.Token);

        var act = async () => await cmd.Consume(input, args, ctx, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
