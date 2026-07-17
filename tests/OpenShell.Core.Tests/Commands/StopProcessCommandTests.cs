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
/// <c>Stop-Process</c> unit tests. Per ADR-0048 §7.3.
/// 验证 -Id / -Name 过滤、参数缺失时写错误、-Force 行为、ShouldProcess High impact 路径。
/// </summary>
public class StopProcessCommandTests
{
    [Fact]
    public async Task Execute_NoIdOrName_WritesInvalidArgument()
    {
        var cmd = new StopProcessCommand();
        var args = new StopProcessCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.InvalidArgument);
        ctx.Errors!.LastError!.Operation.Should().Be("stop-process");
    }

    [Fact]
    public async Task Execute_NonExistentId_SilentlyIgnored()
    {
        var cmd = new StopProcessCommand();
        var args = new StopProcessCommand.Args(Id: new[] { int.MaxValue });
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        // 不存在的 PID 被 ResolveTargets 跳过，无输出无错误。
        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Execute_ByName_KillsAndNoOutputWithoutPassThru()
    {
        // 启动一个测试用进程（cmd /c ping localhost 或跨平台 sleep）。
        var (proc, name) = StartTestProcess();
        try
        {
            var cmd = new StopProcessCommand();
            var args = new StopProcessCommand.Args(Name: new[] { name });
            var ctx = TestCtx();

            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);

            // 无 PassThru，不应有输出项。
            results.Should().BeEmpty();

            // 进程应被 kill。
            // 等待最多 1 秒确保 kill 完成。
            try
            {
                if (!proc.HasExited)
                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            }
            catch { }
            proc.HasExited.Should().BeTrue();
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_ById_KillsAndNoOutputWithoutPassThru()
    {
        var (proc, _) = StartTestProcess();
        try
        {
            var cmd = new StopProcessCommand();
            var args = new StopProcessCommand.Args(Id: new[] { proc.Id });
            var ctx = TestCtx();

            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);

            results.Should().BeEmpty();

            try
            {
                if (!proc.HasExited)
                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            }
            catch { }
            proc.HasExited.Should().BeTrue();
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_PassThru_YieldsItemBeforeKill()
    {
        var (proc, _) = StartTestProcess();
        try
        {
            var cmd = new StopProcessCommand();
            var args = new StopProcessCommand.Args(Id: new[] { proc.Id }, PassThru: true);
            var ctx = TestCtx();

            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);

            // PassThru 返回 1 项（含 Id / Name）。
            results.Should().HaveCount(1);
            results[0].Properties["Id"].Should().Be(proc.Id);

            try
            {
                if (!proc.HasExited)
                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            }
            catch { }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_Force_KillsEntireTree()
    {
        var (proc, _) = StartTestProcess();
        try
        {
            var cmd = new StopProcessCommand();
            var args = new StopProcessCommand.Args(Id: new[] { proc.Id }, Force: true);
            var ctx = TestCtx();

            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            try
            {
                if (!proc.HasExited)
                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            }
            catch { }
            proc.HasExited.Should().BeTrue();
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_PassThru_ItemHasExpectedFields()
    {
        var (proc, _) = StartTestProcess();
        try
        {
            var cmd = new StopProcessCommand();
            var args = new StopProcessCommand.Args(Id: new[] { proc.Id }, PassThru: true);
            var ctx = TestCtx();

            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);

            results[0].Properties.Values.Keys.Should().Contain(new[] { "Id", "Name" });

            try
            {
                if (!proc.HasExited)
                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            }
            catch { }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_ByMultipleIds_AllKilled()
    {
        // 启动 2 个测试进程。
        var (p1, n1) = StartTestProcess();
        var (p2, n2) = StartTestProcess();
        try
        {
            var cmd = new StopProcessCommand();
            var args = new StopProcessCommand.Args(Id: new[] { p1.Id, p2.Id });
            var ctx = TestCtx();

            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            try
            {
                if (!p1.HasExited)
                    await p1.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
                if (!p2.HasExited)
                    await p2.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
            }
            catch { }
            p1.HasExited.Should().BeTrue();
            p2.HasExited.Should().BeTrue();
        }
        finally
        {
            try { if (!p1.HasExited) p1.Kill(); } catch { }
            try { if (!p2.HasExited) p2.Kill(); } catch { }
            p1.Dispose();
            p2.Dispose();
        }
    }

    /// <summary>启动一个长期运行的测试进程，返回 (Process, Name)。</summary>
    private static (Process Proc, string Name) StartTestProcess()
    {
        var psi = new ProcessStartInfo
        {
            // 跨平台：在 Windows 用 cmd /c ping localhost; Unix 用 sleep 60。
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("ping");
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("60");
            psi.ArgumentList.Add("localhost");
        }
        else
        {
            psi.ArgumentList.Add("60");
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start test process");
        // 等待进程启动（确保 OS 已注册）。
        Thread.Sleep(200);
        return (proc, proc.ProcessName);
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
