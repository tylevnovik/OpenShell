using System.Runtime.CompilerServices;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Locations;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Pop-Location</c> unit tests. Per ADR-0048 §3.6.
/// Verifies that the most-recently-pushed location is popped from the host's
/// <see cref="ILocationStack"/> singleton (resolved from <see cref="IHost.Services"/>)
/// and that <see cref="IHost.CurrentLocation"/> is switched to the popped value.
/// Writes an <see cref="ErrorRecord"/> when the stack is empty.
/// </summary>
public class PopLocationCommandTests
{
    [Fact]
    public async Task Execute_WithPushedLocation_SwitchesToPopped()
    {
        var stack = new LocationStack();
        var pushed = ItemPath.Parse("fs::C:/pushed");
        stack.Push(pushed);
        var host = new TestHost(ItemPath.Parse("fs::C:/current"), stack);
        var ctx = TestCtx(host, stack);
        var cmd = new PopLocationCommand();
        var args = new PopLocationCommand.Args();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        host.CurrentLocation.Should().Be(pushed);
        stack.Count.Should().Be(0);
    }

    [Fact]
    public async Task Execute_EmptyStack_WritesError()
    {
        var stack = new LocationStack();
        var host = new TestHost(ItemPath.Parse("fs::C:/current"), stack);
        var ctx = TestCtx(host, stack);
        var cmd = new PopLocationCommand();
        var args = new PopLocationCommand.Args();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.InvalidArgument);
        ctx.Errors!.LastError!.Operation.Should().Be("pop-location");
        host.CurrentLocation.Display.Should().Be("fs::C:/current");
    }

    [Fact]
    public async Task Execute_LifoOrder_LastPushedIsFirstPopped()
    {
        var stack = new LocationStack();
        stack.Push(ItemPath.Parse("fs::C:/first"));
        stack.Push(ItemPath.Parse("fs::C:/second"));
        stack.Push(ItemPath.Parse("fs::C:/third"));
        var host = new TestHost(ItemPath.Parse("fs::C:/current"), stack);
        var ctx = TestCtx(host, stack);
        var cmd = new PopLocationCommand();
        var args = new PopLocationCommand.Args();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.CurrentLocation.Display.Should().Be("fs::C:/third");
        stack.Count.Should().Be(2);
    }

    [Fact]
    public async Task Execute_WritesPoppedDisplayToHostOutput()
    {
        var stack = new LocationStack();
        var pushed = ItemPath.Parse("fs::C:/popped-display");
        stack.Push(pushed);
        var host = new TestHost(ItemPath.Parse("fs::C:/current"), stack);
        var ctx = TestCtx(host, stack);
        var cmd = new PopLocationCommand();
        var args = new PopLocationCommand.Args();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.CapturedOutput.Should().Contain("fs::C:/popped-display");
    }

    [Fact]
    public async Task Execute_YieldsNothing_DoesNotEnterSuccessStream()
    {
        var stack = new LocationStack();
        stack.Push(ItemPath.Parse("fs::C:/x"));
        var host = new TestHost(ItemPath.Parse("fs::C:/current"), stack);
        var ctx = TestCtx(host, stack);
        var cmd = new PopLocationCommand();
        var args = new PopLocationCommand.Args();

        var count = 0;
        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default))
            count++;

        count.Should().Be(0);
    }

    private static CommandContext TestCtx(TestHost host, LocationStack stack)
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = ItemPath.Parse("fs::C:/current"),
            Errors = new InMemoryErrorStream(),
        };
    }

    private sealed class TestHost : OpenShell.IHost
    {
        private readonly LocationStack _stack;
        private readonly List<string> _output = new();
        private ItemPath _current;

        public TestHost(ItemPath initialLocation, LocationStack stack)
        {
            _current = initialLocation;
            _stack = stack;
        }

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get => _current; set => _current = value; }
        public IReadOnlyList<string> CapturedOutput => _output;
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new StackServiceProvider(_stack);

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _output.Add(line);
            return Task.CompletedTask;
        }

        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StackServiceProvider : IServiceProvider
    {
        private readonly LocationStack _stack;
        public StackServiceProvider(LocationStack stack) => _stack = stack;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ILocationStack) ? _stack : null;
    }

    private sealed class EmptyObs<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) { observer.OnCompleted(); return new Disp(); }
    }

    private sealed class Disp : IDisposable { public void Dispose() { } }
}
