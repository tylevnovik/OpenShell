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
/// <c>Test-Path</c> unit tests. Per ADR-0048 §3.1.
/// Verifies existence checks, <c>-PathType</c> narrowing, <c>-IsValid</c> syntax-only mode,
/// and that one result item per input path is yielded.
/// </summary>
public class TestPathCommandTests
{
    [Fact]
    public async Task Execute_ExistingPath_Any_ReturnsTrue()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/dir/file.txt", ItemKind.File);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/dir/file.txt" });
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be(true);
    }

    [Fact]
    public async Task Execute_MissingPath_ReturnsFalse()
    {
        var stub = new StubItemProvider();
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/no/such/path" });
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be(false);
    }

    [Fact]
    public async Task Execute_PathTypeContainer_OnFile_ReturnsFalse()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/dir/file.txt", ItemKind.File);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/dir/file.txt" }, PathType: TestPathType.Container);
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(false);
    }

    [Fact]
    public async Task Execute_PathTypeContainer_OnDirectory_ReturnsTrue()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/dir", ItemKind.Directory);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/dir" }, PathType: TestPathType.Container);
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(true);
    }

    [Fact]
    public async Task Execute_PathTypeLeaf_OnFile_ReturnsTrue()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/file.bin", ItemKind.File);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/file.bin" }, PathType: TestPathType.Leaf);
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(true);
    }

    [Fact]
    public async Task Execute_PathTypeLeaf_OnDirectory_ReturnsFalse()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/dir", ItemKind.Directory);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/dir" }, PathType: TestPathType.Leaf);
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(false);
    }

    [Fact]
    public async Task Execute_IsValid_ValidPath_ReturnsTrue()
    {
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "fs::C:/Users/file.txt" }, IsValid: true);
        var ctx = TestCtx(new StubItemProvider());

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(true);
    }

    [Fact]
    public async Task Execute_IsValid_WhitespacePath_ReturnsFalse()
    {
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "   " }, IsValid: true);
        var ctx = TestCtx(new StubItemProvider());

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(false);
    }

    [Fact]
    public async Task Execute_MultiplePaths_YieldsOnePerInput()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/exists", ItemKind.Directory);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "C:/exists", "C:/missing" });
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Value"].Should().Be(true);
        results[1].Properties["Value"].Should().Be(false);
    }

    [Fact]
    public async Task Execute_NullPath_YieldsNothing()
    {
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: null);
        var ctx = TestCtx(new StubItemProvider());

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_RelativePath_ResolvedAgainstCurrentLocation()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/Users/blmpt/file.txt", ItemKind.File);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "file.txt" });
        var ctx = TestCtx(stub, ItemPath.Parse("fs::C:/Users/blmpt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(true);
    }

    [Fact]
    public async Task Execute_NoItemProvider_WritesErrorAndReturnsFalse()
    {
        // No provider registered: ResolveCapability<IItemProvider> returns null and the
        // command writes a CapabilityNotSupported error and yields a false result.
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: new[] { "fs::C:/x" });
        var ctx = TestCtxWithoutProvider();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be(false);
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.CapabilityNotSupported);
    }

    [Fact]
    public async Task Execute_LiteralPath_TreatedSameAsPath()
    {
        var stub = new StubItemProvider();
        stub.Add("fs::C:/literal.txt", ItemKind.File);
        var cmd = new TestPathCommand();
        var args = new TestPathCommand.Args(Path: null, LiteralPath: new[] { "C:/literal.txt" });
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties["Value"].Should().Be(true);
    }

    private static CommandContext TestCtx(StubItemProvider provider, ItemPath? location = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider);
        return new CommandContext
        {
            Providers = registry,
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = location ?? ItemPath.Parse("fs::/"),
            Errors = new InMemoryErrorStream(),
        };
    }

    private static CommandContext TestCtxWithoutProvider()
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = new InMemoryErrorStream(),
        };
    }

    /// <summary>
    /// In-memory provider implementing <see cref="IItemProvider"/>. Returns the item kind
    /// registered for a given path, or null when the path is unknown.
    /// </summary>
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
            {
                return ValueTask.FromResult<IItem?>(new Item
                {
                    Path = entry.Path,
                    Kind = entry.Kind,
                });
            }
            return ValueTask.FromResult<IItem?>(null);
        }
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
