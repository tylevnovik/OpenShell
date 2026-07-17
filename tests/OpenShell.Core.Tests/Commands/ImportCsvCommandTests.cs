using FluentAssertions;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Import-Csv</c> unit tests. Per ADR-0048 §6.5.
/// 验证 CSV 文件读取、表头识别、-Header 自定义、自定义分隔符、#TYPE 行跳过。
/// </summary>
public class ImportCsvCommandTests
{
    [Fact]
    public async Task Execute_NoCapability_WritesErrorAndYieldsNothing()
    {
        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(Path: ItemPath.Parse("fs::C:/no/file.csv"));
        var ctx = TestCtxWithoutProvider();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.CapabilityNotSupported);
    }

    [Fact]
    public async Task Execute_WithHeader_FirstRowUsedAsHeader()
    {
        using var temp = new TempDir();
        var csvPath = temp.CreateFile("data.csv", "Name,Age\nAlice,30\nBob,40\n");
        var stub = new StubContentProvider();
        stub.Add("fs::" + csvPath.Replace('\\', '/'), "Name,Age\nAlice,30\nBob,40\n");

        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')));
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Name"].Should().Be("Alice");
        results[0].Properties["Age"].Should().Be("30");
        results[1].Properties["Name"].Should().Be("Bob");
    }

    [Fact]
    public async Task Execute_CustomHeader_OverridesFirstRow()
    {
        using var temp = new TempDir();
        var csvPath = temp.CreateFile("data.csv", "Alice,30\nBob,40\n");
        var stub = new StubContentProvider();
        stub.Add("fs::" + csvPath.Replace('\\', '/'), "Alice,30\nBob,40\n");

        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(
            Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')),
            Header: new[] { "Col1", "Col2" });
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Col1"].Should().Be("Alice");
        results[1].Properties["Col1"].Should().Be("Bob");
    }

    [Fact]
    public async Task Execute_CustomDelimiter_SplitsOnIt()
    {
        using var temp = new TempDir();
        var csvPath = temp.CreateFile("data.csv", "Name;Age\nAlice;30\n");
        var stub = new StubContentProvider();
        stub.Add("fs::" + csvPath.Replace('\\', '/'), "Name;Age\nAlice;30\n");

        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(
            Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')),
            Delimiter: ';');
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Name"].Should().Be("Alice");
        results[0].Properties["Age"].Should().Be("30");
    }

    [Fact]
    public async Task Execute_TypeLine_Skipped()
    {
        using var temp = new TempDir();
        var csvPath = temp.CreateFile("data.csv", "#TYPE Foo\nName,Age\nAlice,30\n");
        var stub = new StubContentProvider();
        stub.Add("fs::" + csvPath.Replace('\\', '/'), "#TYPE Foo\nName,Age\nAlice,30\n");

        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')));
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // #TYPE 行被跳过，剩 1 行数据。
        results.Should().HaveCount(1);
        results[0].Properties["Name"].Should().Be("Alice");
    }

    [Fact]
    public async Task Execute_EmptyFile_YieldsNothing()
    {
        using var temp = new TempDir();
        var csvPath = temp.CreateFile("empty.csv", "");
        var stub = new StubContentProvider();
        stub.Add("fs::" + csvPath.Replace('\\', '/'), "");

        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')));
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_QuotedField_UnescapedProperly()
    {
        using var temp = new TempDir();
        var csvContent = "Name,Note\n\"Alice, Jr.\",\"Has \"\"quote\"\"\"\n";
        var csvPath = temp.CreateFile("data.csv", csvContent);
        var stub = new StubContentProvider();
        stub.Add("fs::" + csvPath.Replace('\\', '/'), csvContent);

        var cmd = new ImportCsvCommand();
        var args = new ImportCsvCommand.Args(Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')));
        var ctx = TestCtx(stub);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Name"].Should().Be("Alice, Jr.");
        results[0].Properties["Note"].Should().Be("Has \"quote\"");
    }

    private static CommandContext TestCtx(StubContentProvider provider)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider);
        return new CommandContext
        {
            Providers = registry,
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
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

    /// <summary>Stub 实现 IContentProvider，按 path 返回预存字符串内容。</summary>
    private sealed class StubContentProvider : IProvider, IContentProvider
    {
        private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);

        public ProviderInfo Info { get; } = new()
        {
            Name = "fs",
            Version = new Version(0, 1, 0),
            Description = "Stub content provider",
            Author = "Tests",
        };

        public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
        {
            ProviderCapability.Content,
        };

        public void Add(string displayPath, string content)
        {
            _content[displayPath] = content;
        }

        public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
        {
            if (_content.TryGetValue(path.Display, out var content))
                return ValueTask.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
            return ValueTask.FromResult<Stream>(new MemoryStream());
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
