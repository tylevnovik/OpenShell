using System.Runtime.CompilerServices;
using FluentAssertions;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>ConvertTo-Csv</c> unit tests. Per ADR-0048 §6.3.
/// 验证 IItem → CSV 转换：表头生成、字段转义、自定义分隔符、#TYPE 行、空输入行为。
/// </summary>
public class ConvertToCsvCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args();
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
    public async Task Transform_EmptyInput_YieldsNothing()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(Items(), args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_SingleItem_YieldsHeaderAndRow()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "v1"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 表头 + 1 数据行 = 2 行。
        results.Should().HaveCount(2);
        var header = results[0].Properties["Value"]!.ToString();
        header.Should().Contain("Name");
        header.Should().Contain("Path");
        header.Should().Contain("Kind");
        header.Should().Contain("Size");
        header.Should().Contain("Value");
    }

    [Fact]
    public async Task Transform_NoTypeInformationFalse_AddsTypeLine()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args(NoTypeInformation: false);
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // #TYPE + 表头 + 1 行 = 3 行。
        results.Should().HaveCount(3);
        results[0].Properties["Value"]!.ToString().Should().StartWith("#TYPE ");
    }

    [Fact]
    public async Task Transform_NoTypeInformationTrue_OmitsTypeLine()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args(NoTypeInformation: true);
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Value"]!.ToString().Should().NotStartWith("#TYPE");
    }

    [Fact]
    public async Task Transform_CustomDelimiter_UsesItInHeaderAndRows()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args(Delimiter: ';');
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var header = results[0].Properties["Value"]!.ToString();
        header.Should().Contain(";");
        header.Should().NotContain(",");
    }

    [Fact]
    public async Task Transform_ValueWithComma_EscapedWithQuotes()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "hello,world"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 数据行中含逗号的 Value 字段会被引号包裹。
        var row = results[1].Properties["Value"]!.ToString();
        row.Should().Contain("\"hello,world\"");
    }

    [Fact]
    public async Task Transform_ValueWithQuotes_DoublesQuotes()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: """a"b"""));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var row = results[1].Properties["Value"]!.ToString();
        // 内部引号双写："a""b"。
        row.Should().Contain("\"a\"\"b\"");
    }

    [Fact]
    public async Task Transform_MultipleItems_OneRowPerItem()
    {
        var cmd = new ConvertToCsvCommand();
        var args = new ConvertToCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            Make("a.txt", value: "1"),
            Make("b.txt", value: "2"),
            Make("c.txt", value: "3"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 表头 + 3 数据行 = 4 行。
        results.Should().HaveCount(4);
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
