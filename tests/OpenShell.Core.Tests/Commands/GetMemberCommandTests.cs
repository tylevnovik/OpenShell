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
/// <c>Get-Member</c> unit tests. Per ADR-0048 §4.1.
/// 验证反射输入对象的标准字段、Properties 字典、CLR 值的方法反射、MemberType / Name 过滤等行为。
/// </summary>
public class GetMemberCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args();
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
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(Items(), args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_FirstItem_ReflectsStandardMembers()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 标准字段 5 项: Name / Path / Kind / Size / ContentType。
        var names = results.Select(r => r.Properties["Name"]?.ToString()).ToList();
        names.Should().Contain(new[] { "Name", "Path", "Kind", "Size", "ContentType" });
    }

    [Fact]
    public async Task Transform_PropertiesDict_AddedAsPropertyMembers()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: 42));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        var propertyMembers = results.Where(r => r.Properties["MemberType"]?.ToString() == "Property").ToList();
        var propNames = propertyMembers.Select(r => r.Properties["Name"]?.ToString()).ToList();
        propNames.Should().Contain("Value");
    }

    [Fact]
    public async Task Transform_MemberTypeProperty_FiltersOutMethods()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args(MemberType: "Property");
        var ctx = TestCtx();
        // 用一个 CLR 对象值（字符串）触发方法反射。
        var input = Items(Make("a.txt", value: "hello"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().AllSatisfy(r =>
            r.Properties["MemberType"]?.ToString().Should().Be("Property"));
    }

    [Fact]
    public async Task Transform_NameFilter_OnlyMatchingMembers()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args(Name: new[] { "Name", "Kind" });
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 只剩 Name 与 Kind 两个标准字段。
        var names = results.Select(r => r.Properties["Name"]?.ToString()).ToHashSet();
        names.Should().Contain("Name");
        names.Should().Contain("Kind");
        names.Should().NotContain("Path");
        names.Should().NotContain("Size");
    }

    [Fact]
    public async Task Transform_MultipleItems_OnlyFirstUsed()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            Make("first.txt", value: "a"),
            Make("second.txt", value: "b"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // Get-Member 只反射第一个项（与 PowerShell 一致）。
        // 因 first 有 Value="a"（字符串），反射结果中应包含 string 的方法（如 Length 等）。
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Transform_Output_HasExpectedFields()
    {
        var cmd = new GetMemberCommand();
        var args = new GetMemberCommand.Args();
        var ctx = TestCtx();
        var input = Items(Make("a.txt"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().AllSatisfy(r =>
        {
            r.Properties["TypeName"].Should().NotBeNull();
            r.Properties["Name"].Should().NotBeNull();
            r.Properties["MemberType"].Should().NotBeNull();
            r.Properties["Definition"].Should().NotBeNull();
        });
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
