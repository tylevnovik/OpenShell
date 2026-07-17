using System.Runtime.CompilerServices;
using System.Text;
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
/// <c>Export-Csv</c> unit tests. Per ADR-0048 §6.6.
/// 验证 CSV 文件写入、表头生成、-Append / -NoClobber / -Force / -NoTypeInformation 选项。
/// </summary>
public class ExportCsvCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(Path: ItemPath.Parse("fs::C:/x.csv"));
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
    public async Task Consume_WritesHeaderAndRows()
    {
        using var temp = new TempDir();
        var csvPath = temp.GetFullPath("out.csv");

        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')));
        var ctx = TestCtx();
        var input = Items(
            Make("a.txt", value: "1"),
            Make("b.txt", value: "2"));

        await cmd.Consume(input, args, ctx, default);

        var written = File.ReadAllText(csvPath);
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 表头 + 2 数据行。
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("Name");
        lines[1].Should().Contain("a.txt");
        lines[2].Should().Contain("b.txt");
    }

    [Fact]
    public async Task Consume_NoTypeInformationFalse_WritesTypeLine()
    {
        using var temp = new TempDir();
        var csvPath = temp.GetFullPath("out.csv");

        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(
            Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')),
            NoTypeInformation: false);
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        await cmd.Consume(input, args, ctx, default);

        var written = File.ReadAllText(csvPath);
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // #TYPE + 表头 + 1 行 = 3 行。
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("#TYPE ");
    }

    [Fact]
    public async Task Consume_NoClobberAndFileExists_WritesError()
    {
        using var temp = new TempDir();
        var csvPath = temp.GetFullPath("exists.csv");
        File.WriteAllText(csvPath, "existing");

        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(
            Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')),
            NoClobber: true);
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        await cmd.Consume(input, args, ctx, default);

        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.ItemAlreadyExists);
        // 文件未被覆盖。
        File.ReadAllText(csvPath).Should().Be("existing");
    }

    [Fact]
    public async Task Consume_Append_DoesNotRewriteHeader()
    {
        using var temp = new TempDir();
        var csvPath = temp.GetFullPath("append.csv");
        File.WriteAllText(csvPath, "Name,Path,Kind,Size,Value\nexisting\n");

        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(
            Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')),
            Append: true);
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "1"));

        await cmd.Consume(input, args, ctx, default);

        var written = File.ReadAllText(csvPath);
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 表头保留 + 原有 1 行 + 新 1 行 = 3 行。
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Name");
        lines[1].Should().Be("existing");
        lines[2].Should().Contain("a.txt");
    }

    [Fact]
    public async Task Consume_CustomDelimiter_UsedInOutput()
    {
        using var temp = new TempDir();
        var csvPath = temp.GetFullPath("out.csv");

        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(
            Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')),
            Delimiter: ';');
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "1"));

        await cmd.Consume(input, args, ctx, default);

        var written = File.ReadAllText(csvPath);
        var header = written.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        header.Should().Contain(";");
        header.Should().NotContain(",");
    }

    [Fact]
    public async Task Consume_EscapesCommasInValues()
    {
        using var temp = new TempDir();
        var csvPath = temp.GetFullPath("out.csv");

        var cmd = new ExportCsvCommand();
        var args = new ExportCsvCommand.Args(Path: ItemPath.Parse("fs::" + csvPath.Replace('\\', '/')));
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "hello,world"));

        await cmd.Consume(input, args, ctx, default);

        var written = File.ReadAllText(csvPath);
        written.Should().Contain("\"hello,world\"");
    }

    [Fact]
    public async Task Consume_NonFsProvider_WritesProviderNotFoundError()
    {
        var cmd = new ExportCsvCommand();
        // 使用 zip provider（不存在）。
        var args = new ExportCsvCommand.Args(Path: ItemPath.Parse("zip::archive.zip/file.csv"));
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        await cmd.Consume(input, args, ctx, default);

        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.ProviderNotFound);
    }

    private static IItem Make(string name, object? value = null)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = name },
            Kind = ItemKind.File,
            Properties = value is null
                ? PropertyBag.Empty
                : PropertyBag.Empty.With("Value", value),
        };

    private static async IAsyncEnumerable<IItem> Items(params IItem[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static CommandContext TestCtx()
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
