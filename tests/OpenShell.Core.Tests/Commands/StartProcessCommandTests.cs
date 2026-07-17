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
/// <c>Start-Process</c> unit tests. Per ADR-0048 §7.2.
/// 验证启动外部进程（用 ping / cmd / 等跨平台命令）、-Wait 等待、-PassThru 返回 IItem、
/// -WindowStyle 解析、ShouldProcess 路径。
/// </summary>
public class StartProcessCommandTests
{
    [Fact]
    public async Task Execute_EmptyFilePath_YieldsNothing()
    {
        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(FilePath: "");
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_WithWait_WaitsForExit()
    {
        // 在 Windows 上使用 cmd.exe /c exit 0 启动；跨平台用 ping 是慢路径，cmd 在 Windows 上稳定快。
        var filePath = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var argList = OperatingSystem.IsWindows()
            ? new[] { "/c", "exit", "0" }
            : new[] { "hi" };

        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(
            FilePath: filePath,
            ArgumentList: argList,
            Wait: true);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 无 -PassThru 时不返回项。
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_PassThru_ReturnsProcessItem()
    {
        var filePath = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var argList = OperatingSystem.IsWindows()
            ? new[] { "/c", "exit", "0" }
            : new[] { "hi" };

        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(
            FilePath: filePath,
            ArgumentList: argList,
            Wait: true,
            PassThru: true);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        // PassThru 返回的 IItem 应包含 Id / Name / ExitCode。
        results[0].Properties["Id"].Should().BeAssignableTo<int>();
        results[0].Properties["Name"].Should().NotBeNull();
        // 由于 -Wait，HasExited 应为 true，ExitCode 应为 0。
        results[0].Properties["ExitCode"].Should().Be(0);
    }

    [Fact]
    public async Task Execute_WithWindowStyle_Normal()
    {
        var filePath = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var argList = OperatingSystem.IsWindows()
            ? new[] { "/c", "exit", "0" }
            : new[] { "hi" };

        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(
            FilePath: filePath,
            ArgumentList: argList,
            WindowStyle: "Hidden",
            Wait: true);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 应能正常启动（验证 WindowStyle 不抛异常）。
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_WorkingDirectory_Applied()
    {
        var filePath = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var argList = OperatingSystem.IsWindows()
            ? new[] { "/c", "exit", "0" }
            : new[] { "hi" };
        var tempDir = System.IO.Path.GetTempPath();

        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(
            FilePath: filePath,
            ArgumentList: argList,
            WorkingDirectory: tempDir,
            Wait: true);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 应正常执行（不抛 DirectoryNotFoundException）。
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_WithVerb_UseShellExecute()
    {
        // -Verb 仅在 Windows + UseShellExecute=true 时有效。用 "open" verb 打开 echo/cmd。
        if (!OperatingSystem.IsWindows())
        {
            // 跳过：非 Windows 平台不支持 Verb。
            return;
        }
        var filePath = "cmd.exe";

        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(
            FilePath: filePath,
            ArgumentList: new[] { "/c", "exit", "0" },
            Verb: "open",
            Wait: true);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_PassThru_ItemHasExpectedFields()
    {
        var filePath = OperatingSystem.IsWindows() ? "cmd.exe" : "echo";
        var argList = OperatingSystem.IsWindows()
            ? new[] { "/c", "exit", "0" }
            : new[] { "hi" };

        var cmd = new StartProcessCommand();
        var args = new StartProcessCommand.Args(
            FilePath: filePath,
            ArgumentList: argList,
            Wait: true,
            PassThru: true);
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results[0].Properties.Values.Keys.Should().Contain(new[] { "Id", "Name", "ExitCode" });
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
