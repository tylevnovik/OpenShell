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
/// <c>ConvertFrom-Csv</c> unit tests. Per ADR-0048 §6.4.
/// 验证 CSV 字符串 → IItem 转换：表头 / 自定义 Header、字段转义解析、#TYPE 行跳过、自定义分隔符。
/// 同时验证 ParseCsvLine 内部辅助方法。
/// </summary>
public class ConvertFromCsvCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args();
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
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(Items(), args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_HeaderRow_BecomesPropertyNames()
    {
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            MakeCsvItem("Name,Age"),
            MakeCsvItem("Alice,30"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Name"].Should().Be("Alice");
        results[0].Properties["Age"].Should().Be("30");
    }

    [Fact]
    public async Task Transform_CustomHeader_OverridesFirstRow()
    {
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args(Header: new[] { "Col1", "Col2" });
        var ctx = TestCtx();
        var input = Items(
            MakeCsvItem("Alice,30"),
            MakeCsvItem("Bob,40"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 自定义 Header 后，所有行都视作数据。
        results.Should().HaveCount(2);
        results[0].Properties["Col1"].Should().Be("Alice");
        results[1].Properties["Col1"].Should().Be("Bob");
    }

    [Fact]
    public async Task Transform_QuotedField_UnescapesProperly()
    {
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            MakeCsvItem("Name,Note"),
            MakeCsvItem("\"Alice, Jr.\",\"Has \"\"quote\"\"\""));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Name"].Should().Be("Alice, Jr.");
        results[0].Properties["Note"].Should().Be("Has \"quote\"");
    }

    [Fact]
    public async Task Transform_CustomDelimiter_SplitsOnIt()
    {
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args(Delimiter: ';');
        var ctx = TestCtx();
        var input = Items(
            MakeCsvItem("Name;Age"),
            MakeCsvItem("Alice;30"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Name"].Should().Be("Alice");
        results[0].Properties["Age"].Should().Be("30");
    }

    [Fact]
    public async Task Transform_TypeLine_Skipped()
    {
        var cmd = new ConvertFromCsvCommand();
        var args = new ConvertFromCsvCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            MakeCsvItem("#TYPE Foo"),
            MakeCsvItem("Name,Age"),
            MakeCsvItem("Alice,30"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // #TYPE 行视为数据，但因其以 #TYPE 开头，会跳过表头行 -> 仅 1 行数据。
        // 注意：实际实现中 #TYPE 在 header 之前会跳过，所以表头是第二行 Name,Age。
        results.Should().HaveCount(1);
    }

    [Fact]
    public void ParseCsvLine_SimpleCsv_ReturnsFields()
    {
        var fields = ConvertFromCsvCommand.ParseCsvLine("a,b,c", ',');
        fields.Should().Equal(new[] { "a", "b", "c" });
    }

    [Fact]
    public void ParseCsvLine_QuotedFieldWithDelimiter_KeepsDelimiterInsideQuotes()
    {
        var fields = ConvertFromCsvCommand.ParseCsvLine("\"a,b\",c", ',');
        fields.Should().Equal(new[] { "a,b", "c" });
    }

    [Fact]
    public void ParseCsvLine_DoubledQuotes_UnescapedToSingle()
    {
        var fields = ConvertFromCsvCommand.ParseCsvLine("\"a\"\"b\"", ',');
        fields.Should().Equal(new[] { "a\"b" });
    }

    private static IItem MakeCsvItem(string line)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "csv-line" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", line),
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
