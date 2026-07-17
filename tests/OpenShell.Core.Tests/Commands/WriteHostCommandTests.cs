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
/// <c>Write-Host</c> unit tests. Per ADR-0048 §2.2.
/// Verifies that messages are written to <see cref="IHost.WriteOutputLineAsync"/>
/// and that the command yields no items (host UI only, not the success stream).
/// </summary>
public class WriteHostCommandTests
{
    [Fact]
    public async Task Execute_WritesMessageToHost()
    {
        var host = new CapturingHost();
        var cmd = new WriteHostCommand();
        var args = new WriteHostCommand.Args(Message: "hello-world");
        var ctx = TestCtx(host);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        host.Lines.Should().ContainSingle("hello-world");
    }

    [Fact]
    public async Task Execute_NullMessage_WritesEmptyLine()
    {
        var host = new CapturingHost();
        var cmd = new WriteHostCommand();
        var args = new WriteHostCommand.Args(Message: null);
        var ctx = TestCtx(host);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        host.Lines.Should().ContainSingle()
            .Which.Should().Be(string.Empty);
    }

    [Fact]
    public async Task Execute_GuiHost_FallsBackToWriteOutputLine()
    {
        var host = new CapturingHost(HostKind.Gui);
        var cmd = new WriteHostCommand();
        var args = new WriteHostCommand.Args(Message: "to-gui", NoNewline: true);
        var ctx = TestCtx(host);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        host.Lines.Should().ContainSingle("to-gui");
    }

    [Fact]
    public async Task Execute_NoNewline_GuiHost_WritesAsSingleLine()
    {
        var host = new CapturingHost(HostKind.Gui);
        var cmd = new WriteHostCommand();
        var args = new WriteHostCommand.Args(Message: "compact", NoNewline: true);
        var ctx = TestCtx(host);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.Lines.Should().ContainSingle().Which.Should().Be("compact");
    }

    [Fact]
    public async Task Execute_YieldsNothing_DoesNotEnterSuccessStream()
    {
        var host = new CapturingHost();
        var cmd = new WriteHostCommand();
        var args = new WriteHostCommand.Args(Message: "ignored");
        var ctx = TestCtx(host);

        var count = 0;
        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default))
            count++;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Execute_UnknownColorName_DegradesGracefully()
    {
        var host = new CapturingHost();
        var cmd = new WriteHostCommand();
        var args = new WriteHostCommand.Args(Message: "x", ForegroundColor: "NotAColor");
        var ctx = TestCtx(host);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.Lines.Should().ContainSingle("x");
    }

    private static CommandContext TestCtx(CapturingHost host)
    {
        var errors = new InMemoryErrorStream();
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = errors,
        };
    }

    /// <summary>
    /// Host that records every line written through <see cref="WriteOutputLineAsync"/>
    /// so tests can assert host UI output. Uses an always-redirected Console state so
    /// <see cref="WriteHostCommand"/> takes the non-coloured branch in tests.
    /// </summary>
    private sealed class CapturingHost : OpenShell.IHost
    {
        private readonly List<string> _lines = new();
        private readonly ItemPath _location = ItemPath.Parse("fs::/");

        public CapturingHost(HostKind kind = HostKind.Cli) => Kind = kind;

        public HostKind Kind { get; }
        public ItemPath CurrentLocation { get => _location; set { } }
        public IReadOnlyList<string> Lines => _lines;
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new EmptyServiceProvider();

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _lines.Add(line);
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
