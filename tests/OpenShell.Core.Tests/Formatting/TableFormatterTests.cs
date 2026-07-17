using FluentAssertions;
using OpenShell;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Formatting;

/// <summary>
/// TableFormatter 单元测试。Per ADR-0011, ADR-0033.
/// </summary>
public class TableFormatterTests
{
    private static IItem CreateItem(string name = "file.txt", long? size = 100, ItemKind kind = ItemKind.File)
    {
        var path = ItemPath.Parse($"fs::/tmp/{name}");
        return Item.File(path, size);
    }

    private static async Task<List<string>> CaptureOutputAsync(IAsyncEnumerable<IItem> items, ViewSpec spec)
    {
        var host = new CapturingHost();
        var formatter = new TableFormatter();
        await formatter.FormatAsync(items, spec, host);
        return host.Lines;
    }

    private static async IAsyncEnumerable<IItem> ToStream(IEnumerable<IItem> items,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SupportedKind_IsTable()
    {
        var formatter = new TableFormatter();
        formatter.SupportedKind.Should().Be(ViewKind.Table);
    }

    [Fact]
    public async Task FormatAsync_EmptyItems_ReturnsZero()
    {
        var spec = new ViewSpec { Columns = new[] { new ColumnSpec { Name = "Name" } } };
        var lines = await CaptureOutputAsync(ToStream(Array.Empty<IItem>()), spec);
        // 仅表头 + footer
        lines.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FormatAsync_SingleItem_OutputsOneRow()
    {
        var item = CreateItem("foo.txt", 100);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureOutputAsync(ToStream(new[] { item }), spec);
        lines.Should().Contain(l => l.Contains("foo.txt"));
        lines.Should().Contain(l => l.Contains("100"));
    }

    [Fact]
    public async Task FormatAsync_MultipleItems_OutputsAllRows()
    {
        var items = new[]
        {
            CreateItem("a.txt", 1),
            CreateItem("b.txt", 2),
            CreateItem("c.txt", 3),
        };
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Name" } },
            ShowFooter = false,
        };
        var lines = await CaptureOutputAsync(ToStream(items), spec);
        lines.Should().Contain(l => l.Contains("a.txt"));
        lines.Should().Contain(l => l.Contains("b.txt"));
        lines.Should().Contain(l => l.Contains("c.txt"));
    }

    [Fact]
    public async Task FormatAsync_ShowHeader_OutputsBorderAndHeader()
    {
        var item = CreateItem("foo.txt", 100);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name", DisplayLabel = "Name" },
            },
            ShowHeader = true,
            ShowFooter = false,
        };
        var lines = await CaptureOutputAsync(ToStream(new[] { item }), spec);
        // 至少有一行含 "Name" 表头
        lines.Should().Contain(l => l.Contains("Name"));
        // 至少有一行是边框（含 +---+）
        lines.Should().Contain(l => l.StartsWith("+"));
    }

    [Fact]
    public async Task FormatAsync_ShowFooter_OutputsCountLine()
    {
        var items = new[]
        {
            CreateItem("a.txt", 1),
            CreateItem("b.txt", 2),
        };
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Name" } },
            ShowHeader = false,
            ShowFooter = true,
        };
        var lines = await CaptureOutputAsync(ToStream(items), spec);
        lines.Should().Contain(l => l.Contains("2 item"));
    }

    [Fact]
    public async Task FormatAsync_MaxRowsReached_OutputsTruncatedFooter()
    {
        var items = new[]
        {
            CreateItem("a.txt", 1),
            CreateItem("b.txt", 2),
            CreateItem("c.txt", 3),
        };
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Name" } },
            ShowHeader = false,
            ShowFooter = true,
            MaxRows = 2,
        };
        var lines = await CaptureOutputAsync(ToStream(items), spec);
        lines.Should().Contain(l => l.Contains("truncated"));
    }

    [Fact]
    public async Task FormatAsync_ExplicitColumnWidth_RespectsWidth()
    {
        var item = CreateItem("very-long-name.txt", 100);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name", Width = 5 },
            },
            ShowHeader = false,
            ShowFooter = false,
        };
        var lines = await CaptureOutputAsync(ToStream(new[] { item }), spec);
        // 截断后会带省略号（width >= 3）
        lines.Should().Contain(l => l.Contains("..."));
    }

    [Fact]
    public async Task FormatAsync_AutoDiscoverColumns_UsesSampleProperties()
    {
        var item = CreateItem("foo.txt", 100);
        var spec = new ViewSpec
        {
            Columns = Array.Empty<ColumnSpec>(),
            ShowHeader = true,
            ShowFooter = false,
        };
        var lines = await CaptureOutputAsync(ToStream(new[] { item }), spec);
        // 至少有输出
        lines.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FormatAsync_RightAlignment_PadsLeft()
    {
        var item = CreateItem("x", 100);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name", Width = 10, Align = Alignment.Right },
            },
            ShowHeader = false,
            ShowFooter = false,
        };
        var lines = await CaptureOutputAsync(ToStream(new[] { item }), spec);
        // 右对齐时 x 前应有空格
        lines.Should().Contain(l => l.Contains(" x"));
    }

    /// <summary>最小可观测的 IHost 实现，捕获 WriteOutputLineAsync 写入的所有行。</summary>
    private sealed class CapturingHost : IHost
    {
        public List<string> Lines { get; } = new();

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Root("fs");
        public IObservable<IReadOnlyList<IItem>> Selection { get; } = new EmptyObservable();
        public IProgress<OperationProgress> Progress { get; } = new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }

        public async Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
        {
            await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Lines.Add(item.Name);
            }
        }
    }

    private sealed class EmptyObservable : IObservable<IReadOnlyList<IItem>>
    {
        public IDisposable Subscribe(IObserver<IReadOnlyList<IItem>> observer)
        {
            observer.OnCompleted();
            return new EmptyDisposable();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
