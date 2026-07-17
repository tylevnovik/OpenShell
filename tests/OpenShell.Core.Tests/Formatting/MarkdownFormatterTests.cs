using FluentAssertions;
using OpenShell;
using OpenShell.Commands.Builtins;
using OpenShell.Commands;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Formatting;

/// <summary>
/// MarkdownFormatter 单元测试。Per ADR-0011 §7 / ADR-0033.
/// 覆盖空列表、单/多行、列对齐启发式、转义、null/bool/数值格式化、混合类型、自定义列、Format-Markdown 命令。
/// </summary>
public class MarkdownFormatterTests
{
    /// <summary>
    /// 构造一个测试用 IItem。Properties 通过 params 传入的 (key, value) 对构造。
    /// </summary>
    private static IItem MakeItem(
        string name = "file.txt",
        long? size = 100,
        DateTimeOffset? modified = null,
        params (string Key, object? Value)[] props)
    {
        var path = ItemPath.Parse($"fs::/tmp/{name}");
        var bag = PropertyBag.Empty;
        foreach (var (k, v) in props)
        {
            bag = bag.With(k, v);
        }
        return new Item
        {
            Path = path,
            Kind = ItemKind.File,
            Size = size,
            Timestamps = new ItemTimestamps(null, modified, null),
            Properties = bag,
        };
    }

    private static async Task<List<string>> CaptureAsync(IAsyncEnumerable<IItem> items, ViewSpec spec)
    {
        var host = new CapturingHost();
        var formatter = new MarkdownFormatter();
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
    public async Task SupportedKind_IsMarkdown()
    {
        var formatter = new MarkdownFormatter();
        formatter.SupportedKind.Should().Be(ViewKind.Markdown);
    }

    [Fact]
    public async Task EmptyList_EmitsOnlyHeaderAndSeparator()
    {
        // 空流：仅输出表头 + 分隔行（无数据行）。即使 ShowFooter=true 也不应输出 footer（emitted=0 非 truncated）。
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
                new ColumnSpec { Name = "LastModified" },
            },
            ShowFooter = true,
        };
        var lines = await CaptureAsync(ToStream(Array.Empty<IItem>()), spec);

        // 仅 2 行：表头 + 分隔行。
        lines.Should().HaveCount(2);
        lines[0].Should().Be("| Name | Size | LastModified |");
        // 三列均无样本数据 → "unknown → left-align"，分隔符为 "---"。
        lines[1].Should().Be("| --- | --- | --- |");
    }

    [Fact]
    public async Task SingleItem_OutputsHeaderSeparatorAndOneRow()
    {
        var item = MakeItem("file1.txt", 1024);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // 3 行：表头 + 分隔 + 数据。
        lines.Should().HaveCount(3);
        lines[0].Should().Be("| Name | Size |");
        // Size 是 long → 数值 → 右对齐 "---:"。
        lines[1].Should().Be("| --- | ---: |");
        lines[2].Should().Be("| file1.txt | 1024 |");
    }

    [Fact]
    public async Task MultipleItems_OutputsAllRows()
    {
        var items = new[]
        {
            MakeItem("file1.txt", 1024),
            MakeItem("file2.log", 2048),
        };
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(items), spec);

        // 4 行：表头 + 分隔 + 2 数据。
        lines.Should().HaveCount(4);
        lines[2].Should().Be("| file1.txt | 1024 |");
        lines[3].Should().Be("| file2.log | 2048 |");
    }

    [Fact]
    public async Task RightAlign_NumericColumns_UseDashColonSeparator()
    {
        var items = new[]
        {
            MakeItem("a.txt", 100),
            MakeItem("b.txt", 200),
        };
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
                new ColumnSpec { Name = "Score" }, // 走 Properties 字典
            },
            ShowFooter = false,
        };
        // 给两个 item 都加 Score 属性（int）。
        items[0] = MakeItem("a.txt", 100, props: ("Score", 95));
        items[1] = MakeItem("b.txt", 200, props: ("Score", 87));

        var lines = await CaptureAsync(ToStream(items), spec);

        // Name → 左对齐 "---"；Size/Score → 右对齐 "---:"。
        lines[1].Should().Be("| --- | ---: | ---: |");
    }

    [Fact]
    public async Task LeftAlign_StringColumns_UseDashSeparator()
    {
        var items = new[]
        {
            MakeItem("a.txt", 100, props: ("Owner", "alice")),
            MakeItem("b.txt", 200, props: ("Owner", "bob")),
        };
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Owner" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(items), spec);

        // 两列均为 string → 左对齐。
        lines[1].Should().Be("| --- | --- |");
        lines[2].Should().Be("| a.txt | alice |");
    }

    [Fact]
    public async Task EscapePipeInCellValue_IsBackslashPiped()
    {
        var item = MakeItem("a|b.txt", 100);
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Name" } },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // 单元格内的 | 必须转义为 \|。
        lines[2].Should().Be("| a\\|b.txt |");
    }

    [Fact]
    public async Task EscapeNewlineInCellValue_IsBrTag()
    {
        // Properties 字典中放一个含换行的字符串值。
        var item = MakeItem("a.txt", 100, props: ("Note", "line1\nline2"));
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Note" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // \n 必须替换为 <br>。
        lines[2].Should().Be("| a.txt | line1<br>line2 |");
    }

    [Fact]
    public async Task EscapeCrlfInCellValue_BecomesSingleBr()
    {
        var item = MakeItem("a.txt", 100, props: ("Note", "x\r\ny"));
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Note" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // CRLF 应合并为单个 <br>（CR 丢弃，LF → <br>）。
        lines[2].Should().Be("| a.txt | x<br>y |");
    }

    [Fact]
    public async Task NullValue_RendersAsEmptyCell()
    {
        // Size 为 null（目录）。
        var item = MakeItem("dir1", size: null);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // Size 列值为 null → 空字符串。但因无值，对齐退化为 Left。
        lines[1].Should().Be("| --- | --- |");
        lines[2].Should().Be("| dir1 |  |");
    }

    [Fact]
    public async Task BooleanValue_RendersAsLowercase()
    {
        var item = MakeItem("a.txt", 100, props: ("Active", true));
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Active" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // bool 必须小写 "true"/"false"，且视为非数值 → 左对齐。
        lines[1].Should().Be("| --- | --- |");
        lines[2].Should().Be("| a.txt | true |");
    }

    [Fact]
    public async Task BooleanFalse_RendersAsLowercaseFalse()
    {
        var item = MakeItem("a.txt", 100, props: ("Active", false));
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Active" } },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        lines[2].Should().Be("| false |");
    }

    [Fact]
    public async Task NumberFormatting_UsesInvariantCulture()
    {
        // 1234567 在 InvariantCulture 下应为 "1234567"（无千分位逗号）。
        // 注意：ItemValueAccessor 默认用 N0 → "1,234,567"；MarkdownFormatter 必须用 plain ToString。
        var item = MakeItem("big.txt", 1234567L);
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Size" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        lines[2].Should().Be("| big.txt | 1234567 |");
    }

    [Fact]
    public async Task DoubleValue_UsesInvariantCulture()
    {
        // double 也用 InvariantCulture，确保小数点为 "." 而非某些 locale 的 ","。
        var item = MakeItem("pi.txt", 100, props: ("Ratio", 3.14));
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Ratio" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // Ratio 是 double → 数值 → 右对齐；值用 InvariantCulture。
        lines[1].Should().Be("| --- | ---: |");
        lines[2].Should().Be("| pi.txt | 3.14 |");
    }

    [Fact]
    public async Task MixedColumnTypes_FallbackToLeftAlign()
    {
        // 同一列中第一个值是 int，第二个值是 string → 混合 → 左对齐。
        var items = new[]
        {
            MakeItem("a.txt", 100, props: ("Mixed", 42)),
            MakeItem("b.txt", 200, props: ("Mixed", "hello")),
        };
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Name" },
                new ColumnSpec { Name = "Mixed" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(items), spec);

        // Mixed 列因混合类型 → 左对齐 "---"。
        lines[1].Should().Be("| --- | --- |");
        lines[2].Should().Be("| a.txt | 42 |");
        lines[3].Should().Be("| b.txt | hello |");
    }

    [Fact]
    public async Task CustomViewSpecColumns_RoundTripPreservesColumnOrder()
    {
        // 用户显式指定列顺序与 DisplayLabel，输出应严格按之。
        var modified = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        var item = MakeItem("file.txt", 1024, modified, props: ("Owner", "alice"));
        var spec = new ViewSpec
        {
            Columns = new[]
            {
                new ColumnSpec { Name = "Owner", DisplayLabel = "Owner Name" },
                new ColumnSpec { Name = "Name", DisplayLabel = "File" },
                new ColumnSpec { Name = "Size", DisplayLabel = "Bytes" },
            },
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // 表头使用 DisplayLabel。
        lines[0].Should().Be("| Owner Name | File | Bytes |");
        // Owner → string → 左；Name → string → 左；Size → long → 右。
        lines[1].Should().Be("| --- | --- | ---: |");
        lines[2].Should().Be("| alice | file.txt | 1024 |");
    }

    [Fact]
    public async Task AutoDiscoverColumns_UsesFirstItemProperties()
    {
        // 不指定 Columns：自动发现 Name/Size/Modified + Properties.Keys（按字典序）。
        var modified = new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
        var item = MakeItem("file1.txt", 1024, modified, ("Zebra", "z"), ("Alpha", "a"));
        var spec = new ViewSpec
        {
            Columns = Array.Empty<ColumnSpec>(),
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // 自动发现列：Name, Size, Modified, Alpha, Zebra（Properties.Keys 按字典序）。
        lines[0].Should().Be("| Name | Size | Modified | Alpha | Zebra |");
        // Name → string → 左；Size → long → 右；Modified → date → 左；Alpha/Zebra → string → 左。
        lines[1].Should().Be("| --- | ---: | --- | --- | --- |");
        lines[2].Should().Contain("file1.txt");
        lines[2].Should().Contain("1024");
        lines[2].Should().Contain("z");
        lines[2].Should().Contain("a");
    }

    [Fact]
    public async Task MaxRows_TruncatesAndEmitsCommentFooter()
    {
        var items = new[]
        {
            MakeItem("a.txt", 1),
            MakeItem("b.txt", 2),
            MakeItem("c.txt", 3),
        };
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Name" } },
            ShowFooter = true,
            MaxRows = 2,
        };
        var lines = await CaptureAsync(ToStream(items), spec);

        // 表头 + 分隔 + 2 数据 + truncated 注释 = 5 行。
        lines.Should().HaveCount(5);
        lines[4].Should().Contain("truncated");
        // 应为 HTML 注释形式（Markdown 友好）。
        lines[4].Should().StartWith("<!--").And.EndWith("-->");
    }

    [Fact]
    public async Task ShowHeaderFalse_OmitsHeaderAndSeparator()
    {
        var item = MakeItem("a.txt", 1);
        var spec = new ViewSpec
        {
            Columns = new[] { new ColumnSpec { Name = "Name" } },
            ShowHeader = false,
            ShowFooter = false,
        };
        var lines = await CaptureAsync(ToStream(new[] { item }), spec);

        // 仅 1 行数据。
        lines.Should().HaveCount(1);
        lines[0].Should().Be("| a.txt |");
    }

    [Fact]
    public async Task FormatMarkdownCommand_Consume_YieldsMarkdownItems()
    {
        // 测试 Format-Markdown 命令通过 IPipelineSink.Consume 接口消费 IItem 流，
        // 验证输出确实为 Markdown 表格格式（表头 + 分隔 + 数据行）。
        var items = new[]
        {
            MakeItem("file1.txt", 1024),
            MakeItem("file2.log", 2048),
        };
        var host = new CapturingHost();
        var ctx = new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = ItemPath.Root("fs"),
        };
        var cmd = new FormatMarkdownCommand();
        var args = new FormatMarkdownCommand.Args
        {
            Properties = new[] { "Name", "Size" },
        };

        await cmd.Consume(ToStream(items), args, ctx, default);

        host.Lines.Should().NotBeEmpty();
        // 第一行必须是表头。
        host.Lines[0].Should().Be("| Name | Size |");
        // 第二行必须是分隔行（Size 数值列 → 右对齐）。
        host.Lines[1].Should().Be("| --- | ---: |");
        // 数据行必须包含两项的名字与大小。
        host.Lines.Should().Contain(l => l.Contains("file1.txt") && l.Contains("1024"));
        host.Lines.Should().Contain(l => l.Contains("file2.log") && l.Contains("2048"));
        // ExecuteAsync 应抛 NotSupportedException（pipeline-only）。
        Assert.Throws<NotSupportedException>(() => cmd.ExecuteAsync(args, ctx));
    }

    [Fact]
    public async Task FormatMarkdownCommand_DefaultProperties_AutoDiscoversColumns()
    {
        // 不传 Properties：自动发现首项 Properties.Keys + Name/Size/Modified。
        var item = MakeItem("auto.txt", 512, props: ("Owner", "alice"));
        var host = new CapturingHost();
        var ctx = new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = ItemPath.Root("fs"),
        };
        var cmd = new FormatMarkdownCommand();
        var args = new FormatMarkdownCommand.Args();

        await cmd.Consume(ToStream(new[] { item }), args, ctx, default);

        // 表头应包含自动发现的 Owner 列。
        host.Lines[0].Should().Contain("Name");
        host.Lines[0].Should().Contain("Size");
        host.Lines[0].Should().Contain("Owner");
        // 数据行应包含 alice。
        host.Lines.Should().Contain(l => l.Contains("alice"));
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
