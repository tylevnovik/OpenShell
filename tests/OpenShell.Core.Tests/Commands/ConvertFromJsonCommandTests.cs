using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// <c>ConvertFrom-Json</c> unit tests. Per ADR-0048 §6.2.
/// 验证 JSON 字符串 → IItem 转换：对象 / 数组 / 基本类型、嵌套结构、-InputObject 直接绑定等。
/// </summary>
public class ConvertFromJsonCommandTests
{
    [Fact]
    public async Task Transform_PipeJsonObject_YieldsSingleItemWithProperties()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(MakeJsonItem("""{"name":"alice","age":30}"""));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["name"].Should().Be("alice");
        results[0].Properties["age"].Should().Be(30L);
    }

    [Fact]
    public async Task Transform_PipeJsonArray_YieldsMultipleItems()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(MakeJsonItem("""[1,2,3]"""));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(3);
        results[0].Properties["Value"].Should().Be(1L);
        results[1].Properties["Value"].Should().Be(2L);
        results[2].Properties["Value"].Should().Be(3L);
    }

    [Fact]
    public async Task Transform_PipeJsonString_YieldsValueItem()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(MakeJsonItem("\"hello\""));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("hello");
    }

    [Fact]
    public async Task Transform_PipeJsonBool_YieldsValueItem()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(MakeJsonItem("true"), MakeJsonItem("false"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Properties["Value"].Should().Be(true);
        results[1].Properties["Value"].Should().Be(false);
    }

    [Fact]
    public async Task Transform_PipeJsonNull_YieldsValueItemWithNull()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(MakeJsonItem("null"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WithInputObject_BindsDirectly()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args(InputObject: """{"key":"value"}""");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["key"].Should().Be("value");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyInput_YieldsNothing()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args(InputObject: null);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_NestedObject_PropertyValueIsDictionary()
    {
        var cmd = new ConvertFromJsonCommand();
        var args = new ConvertFromJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(MakeJsonItem("""{"outer":{"inner":"v"}}"""));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        var nested = results[0].Properties["outer"] as System.Collections.IDictionary;
        nested.Should().NotBeNull();
        nested!["inner"].Should().Be("v");
    }

    private static IItem MakeJsonItem(string json)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "json" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", json),
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
