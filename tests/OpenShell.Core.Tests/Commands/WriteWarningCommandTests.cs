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
/// <c>Write-Warning</c> unit tests. Per ADR-0048 §2.4.
/// Verifies that a warning is written to the host UI prefixed with <c>WARNING:</c>
/// and that nothing is yielded to the success stream.
/// </summary>
public class WriteWarningCommandTests
{
    [Fact]
    public async Task Execute_WritesWarningPrefixedLine()
    {
        var host = new CapturingHost();
        var cmd = new WriteWarningCommand();
        var args = new WriteWarningCommand.Args(Message: "disk almost full");
        var ctx = TestCtx(host);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        host.Lines.Should().ContainSingle()
            .Which.Should().Contain("WARNING:")
            .And.Contain("disk almost full");
    }

    [Fact]
    public async Task Execute_NullMessage_WritesEmptyWarning()
    {
        var host = new CapturingHost();
        var cmd = new WriteWarningCommand();
        var args = new WriteWarningCommand.Args(Message: null);
        var ctx = TestCtx(host);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.Lines.Should().ContainSingle()
            .Which.Should().StartWith("WARNING:");
    }

    [Fact]
    public async Task Execute_YieldsNothing_DoesNotEnterSuccessStream()
    {
        var host = new CapturingHost();
        var cmd = new WriteWarningCommand();
        var args = new WriteWarningCommand.Args(Message: "ignored");
        var ctx = TestCtx(host);

        var count = 0;
        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default))
            count++;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Execute_GuiHost_EmitsPlainPrefixWithoutAnsi()
    {
        // When the host is GUI (or output is redirected), the ANSI colour branch is
        // skipped and the plain "WARNING: ..." line is emitted.
        var host = new CapturingHost(HostKind.Gui);
        var cmd = new WriteWarningCommand();
        var args = new WriteWarningCommand.Args(Message: "x");
        var ctx = TestCtx(host);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.Lines.Should().ContainSingle()
            .Which.Should().NotContain("\u001b[")
            .And.Contain("WARNING: x");
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
