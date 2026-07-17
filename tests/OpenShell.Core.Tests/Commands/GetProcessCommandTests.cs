using System.Diagnostics;
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
/// <c>Get-Process</c> unit tests. Per ADR-0048 §7.1.
/// 验证进程枚举、-Id 过滤、-Name 通配符过滤、输出 IItem 属性（Id/Name/CPU/WS/PM/Path）。
/// </summary>
public class GetProcessCommandTests
{
    [Fact]
    public async Task Execute_NoArgs_YieldsAllProcesses()
    {
        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 至少应能枚举到当前进程。
        results.Should().NotBeEmpty();
        // 输出项应包含 Id / Name 属性。
        results[0].Properties["Id"].Should().BeAssignableTo<int>();
        results[0].Properties["Name"].Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_FilterById_ReturnsOnlyMatchingProcess()
    {
        // 用当前进程 ID 作为目标。
        var currentId = Environment.ProcessId;

        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args(Id: new[] { currentId });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Properties["Id"].Should().Be(currentId));
    }

    [Fact]
    public async Task Execute_FilterByNonExistentId_YieldsNothing()
    {
        var cmd = new GetProcessCommand();
        // PID 0 通常为 System Idle，但权限受限，可能仍能枚举到。用一个绝对不存在的 PID（int.MaxValue）。
        var args = new GetProcessCommand.Args(Id: new[] { int.MaxValue });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_FilterByName_ExactMatch_ReturnsCurrentProcessName()
    {
        var currentName = Process.GetCurrentProcess().ProcessName;

        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args(Name: new[] { currentName });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Properties["Name"].Should().Be(currentName));
    }

    [Fact]
    public async Task Execute_FilterByName_WildcardStar()
    {
        var currentName = Process.GetCurrentProcess().ProcessName;
        var prefix = currentName.Length > 3 ? currentName[..3] : currentName;
        var pattern = prefix + "*";

        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args(Name: new[] { pattern });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 至少当前进程应当匹配前缀*模式。
        results.Should().NotBeEmpty();
        var names = results.Select(r => r.Properties["Name"]?.ToString()).ToList();
        names.Should().Contain(currentName);
    }

    [Fact]
    public async Task Execute_FilterByName_QuestionMarkSingleChar()
    {
        var currentName = Process.GetCurrentProcess().ProcessName;
        // 用 ? 替换第 2 个字符作为模式。
        if (currentName.Length < 2)
        {
            // 跳过：当前进程名太短，无法测试 ? 通配符。
            return;
        }
        var pattern = currentName[0] + "?" + currentName[2..];

        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args(Name: new[] { pattern });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 当前进程应匹配 (one-char-wildcard pattern)。
        var names2 = results.Select(r => r.Properties["Name"]?.ToString()).ToList();
        names2.Should().Contain(currentName);
    }

    [Fact]
    public async Task Execute_FilterByMultipleNames_ReturnsUnion()
    {
        var currentName = Process.GetCurrentProcess().ProcessName;

        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args(Name: new[] { "definitely-no-such-process", currentName });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 第二个 name 应匹配，第一个不匹配。
        results.Should().NotBeEmpty();
        var names3 = results.Select(r => r.Properties["Name"]?.ToString()).ToList();
        names3.Should().Contain(currentName);
    }

    [Fact]
    public async Task Execute_Output_HasExpectedProcessFields()
    {
        var currentId = Environment.ProcessId;

        var cmd = new GetProcessCommand();
        var args = new GetProcessCommand.Args(Id: new[] { currentId });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties.Values.Keys.Should().Contain(new[] { "Id", "Name", "CPU", "WS", "PM", "Path" });
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
