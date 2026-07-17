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
/// <c>ConvertTo-Json</c> unit tests. Per ADR-0048 §6.1.
/// 验证 IItem → JSON 字符串序列化、Depth / Compress / AsArray 选项、单项 / 多项 / 空输入行为。
/// </summary>
public class ConvertToJsonCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args();
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
    public async Task Transform_EmptyInput_ReturnsNull()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(Items(), args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        results[0].Properties["Value"].Should().Be("null");
    }

    [Fact]
    public async Task Transform_SingleItem_ReturnsJsonObject()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "hello"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        var json = results[0].Properties["Value"]!.ToString()!;
        var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("name").GetString().Should().Be("a.txt");
        doc.RootElement.GetProperty("value").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task Transform_MultipleItems_ReturnsJsonArray()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            Make("a.txt", value: "first"),
            Make("b.txt", value: "second"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        var json = results[0].Properties["Value"]!.ToString()!;
        var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Transform_AsArray_SingleItem_WrappedAsArray()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args(AsArray: true);
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "solo"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var json = results[0].Properties["Value"]!.ToString()!;
        var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Transform_Compress_NoIndentation()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args(Compress: true);
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "v"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var json = results[0].Properties["Value"]!.ToString();
        // 压缩模式无缩进（不含 \n  + 空格）。
        json.Should().NotContain("\n  ");
    }

    [Fact]
    public async Task Transform_NoCompress_HasIndentation()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args(Compress: false);
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "v"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var json = results[0].Properties["Value"]!.ToString();
        json.Should().Contain("\n");
    }

    [Fact]
    public async Task Transform_CamelCase_PropertyNamingPolicy()
    {
        var cmd = new ConvertToJsonCommand();
        var args = new ConvertToJsonCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var json = results[0].Properties["Value"]!.ToString();
        // 标准字段已使用小写 name/path/kind/size（per ADR-0022）。
        json.Should().Contain("\"name\"");
        json.Should().Contain("\"path\"");
        json.Should().Contain("\"kind\"");
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
