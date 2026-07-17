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
/// <c>Push-Location</c> unit tests. Per ADR-0048 §3.6.
/// Verifies that the current location is pushed onto the host's <see cref="ILocationStack"/>
/// singleton (resolved from <see cref="IHost.Services"/>), and that the host's
/// <see cref="IHost.CurrentLocation"/> is updated to the target.
/// </summary>
public class PushLocationCommandTests
{
    [Fact]
    public async Task Execute_WithTarget_PushesCurrentAndSwitches()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/target", ItemKind.Directory);
        var (stack, host, ctx) = Setup(
            initialLocation: ItemPath.Parse("fs::C:/start"),
            stub);

        var cmd = new PushLocationCommand();
        var args = new PushLocationCommand.Args(Path: ItemPath.Parse("C:/target"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        stack.Count.Should().Be(1);
        stack.Pop().Should().Be(ItemPath.Parse("fs::C:/start"));
        host.CurrentLocation.Display.Should().Be("fs::C:/target");
    }

    [Fact]
    public async Task Execute_WithoutPath_PushesCurrentAndSwitchesToProviderRoot()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::/", ItemKind.Directory);
        var (stack, host, ctx) = Setup(
            initialLocation: ItemPath.Parse("fs::C:/start"),
            stub);

        var cmd = new PushLocationCommand();
        var args = new PushLocationCommand.Args(Path: null);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        stack.Count.Should().Be(1);
        stack.Pop().Should().Be(ItemPath.Parse("fs::C:/start"));
        host.CurrentLocation.Provider.Should().Be("fs");
        host.CurrentLocation.InternalPath.Should().Be("/");
    }

    [Fact]
    public async Task Execute_MissingTarget_WritesItemNotFound()
    {
        // No item registered at C:/missing -> provider returns null -> ItemNotFound error.
        var stub = new StubItemProvider();
        var (stack, host, ctx) = Setup(
            initialLocation: ItemPath.Parse("fs::C:/start"),
            stub);

        var cmd = new PushLocationCommand();
        var args = new PushLocationCommand.Args(Path: ItemPath.Parse("C:/missing"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        // Even on failure, the current location is pushed (matching PowerShell's behaviour).
        stack.Count.Should().Be(1);
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.ItemNotFound);
        // Host location unchanged.
        host.CurrentLocation.Display.Should().Be("fs::C:/start");
    }

    [Fact]
    public async Task Execute_NoItemProvider_WritesCapabilityNotSupported()
    {
        var (stack, host, ctx) = Setup(
            initialLocation: ItemPath.Parse("fs::C:/start"),
            stub: null);

        var cmd = new PushLocationCommand();
        var args = new PushLocationCommand.Args(Path: ItemPath.Parse("C:/target"));

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.CapabilityNotSupported);
        host.CurrentLocation.Display.Should().Be("fs::C:/start");
    }

    [Fact]
    public async Task Execute_RelativePath_ResolvedAgainstCurrentLocation()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/parent/sub", ItemKind.Directory);
        var (stack, host, ctx) = Setup(
            initialLocation: ItemPath.Parse("fs::C:/parent"),
            stub);

        var cmd = new PushLocationCommand();
        var args = new PushLocationCommand.Args(Path: ItemPath.Parse("sub"));

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.CurrentLocation.Display.Should().Be("fs::C:/parent/sub");
        stack.Count.Should().Be(1);
        stack.Pop().Should().Be(ItemPath.Parse("fs::C:/parent"));
    }

    [Fact]
    public async Task Execute_WritesTargetDisplayToHostOutput()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/target", ItemKind.Directory);
        var (stack, host, ctx) = Setup(
            initialLocation: ItemPath.Parse("fs::C:/start"),
            stub);

        var cmd = new PushLocationCommand();
        var args = new PushLocationCommand.Args(Path: ItemPath.Parse("C:/target"));

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.CapturedOutput.Should().Contain("fs::C:/target");
    }

    private static (LocationStack stack, TestHost host, CommandContext ctx) Setup(
        ItemPath initialLocation,
        StubItemProvider? stub)
    {
        var stack = new LocationStack();
        var host = new TestHost(initialLocation, stack);
        var registry = new ProviderRegistry();
        if (stub is not null) registry.Register(stub);
        var ctx = new CommandContext
        {
            Providers = registry,
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = initialLocation,
            Errors = new InMemoryErrorStream(),
        };
        return (stack, host, ctx);
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

    private sealed class StubItemProvider : IProvider, IItemProvider
    {
        private readonly Dictionary<string, (ItemPath Path, ItemKind Kind)> _items = new(StringComparer.OrdinalIgnoreCase);

        public ProviderInfo Info { get; } = new()
        {
            Name = "fs",
            Version = new Version(0, 1, 0),
            Description = "Stub item provider for unit tests",
            Author = "OpenShell.Core.Tests",
        };

        public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
        {
            ProviderCapability.Item,
        };

        public void Add(string displayPath, ItemKind kind)
        {
            var path = ItemPath.Parse(displayPath);
            _items[path.InternalPath] = (path, kind);
        }

        public ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default)
        {
            if (_items.TryGetValue(path.InternalPath, out var entry))
                return ValueTask.FromResult<IItem?>(new Item { Path = entry.Path, Kind = entry.Kind });
            return ValueTask.FromResult<IItem?>(null);
        }
    }
}
