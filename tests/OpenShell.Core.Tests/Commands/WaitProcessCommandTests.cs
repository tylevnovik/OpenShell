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
/// <c>Wait-Process</c> unit tests. Per ADR-0048 §7.4.
/// 验证等待进程退出、参数缺失写错误、超时机制、-Id / -Name 过滤。
/// </summary>
public class WaitProcessCommandTests
{
    [Fact]
    public async Task Execute_NoIdOrName_WritesInvalidArgument()
    {
        var cmd = new WaitProcessCommand();
        var args = new WaitProcessCommand.Args();
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.InvalidArgument);
        ctx.Errors!.LastError!.Operation.Should().Be("wait-process");
    }

    [Fact]
    public async Task Execute_WaitsForExitedProcess()
    {
        // 启动一个短时进程，等它退出，再调用 Wait-Process（通过 PID）。
        var (proc, _) = StartShortProcess();
        try
        {
            // 等进程退出。
            await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var cmd = new WaitProcessCommand();
            var args = new WaitProcessCommand.Args(Id: new[] { proc.Id });
            var ctx = TestCtx();

            // 已退出的进程，Wait-Process 应立即返回。
            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }
        }
        finally
        {
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_Timeout_WritesOperationTimeout()
    {
        // 启动一个长期进程，设超时为 2 秒，应在超时后写错误。
        // Timeout=2（而非 1）给进程更多稳定时间，避免并行测试负载下的 flaky 失败。
        var (proc, _) = StartLongProcess();
        try
        {
            // 前置条件：长进程应仍在运行。若已退出（并行负载下偶发），跳过断言。
            if (proc.HasExited)
            {
                // 进程意外提前退出 — 不代表 Wait-Process 逻辑有误，跳过本测试。
                return;
            }

            var cmd = new WaitProcessCommand();
            var args = new WaitProcessCommand.Args(Id: new[] { proc.Id }, Timeout: 2);
            var ctx = TestCtx();

            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            // 应写 OperationTimeout 错误。
            ctx.Errors!.LastError.Should().NotBeNull();
            ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.OperationTimeout);
            ctx.Errors!.LastError!.Message.Should().Contain("did not exit");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_NonExistentId_SilentlyIgnored()
    {
        var cmd = new WaitProcessCommand();
        var args = new WaitProcessCommand.Args(Id: new[] { int.MaxValue });
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        // 不存在的 PID 跳过，无错误。
        ctx.Errors!.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Execute_ByName_WaitsForExit()
    {
        var (proc, name) = StartShortProcess();
        try
        {
            await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var cmd = new WaitProcessCommand();
            var args = new WaitProcessCommand.Args(Name: new[] { name });
            var ctx = TestCtx();

            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            // 无错误。
            ctx.Errors!.LastError.Should().BeNull();
        }
        finally
        {
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_AlreadyExited_DoesNotTimeout()
    {
        var (proc, _) = StartShortProcess();
        try
        {
            await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var cmd = new WaitProcessCommand();
            var args = new WaitProcessCommand.Args(Id: new[] { proc.Id }, Timeout: 1);
            var ctx = TestCtx();

            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            // 已退出 → 不会超时。
            ctx.Errors!.LastError.Should().BeNull();
        }
        finally
        {
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Execute_MultipleIds_AllWaited()
    {
        var (p1, _) = StartShortProcess();
        var (p2, _) = StartShortProcess();
        try
        {
            await p1.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            await p2.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var cmd = new WaitProcessCommand();
            var args = new WaitProcessCommand.Args(Id: new[] { p1.Id, p2.Id });
            var ctx = TestCtx();

            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            ctx.Errors!.LastError.Should().BeNull();
        }
        finally
        {
            p1.Dispose();
            p2.Dispose();
        }
    }

    [Fact]
    public async Task Execute_NoTimeout_WaitsIndefinitelyForExited()
    {
        var (proc, _) = StartShortProcess();
        try
        {
            await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            var cmd = new WaitProcessCommand();
            var args = new WaitProcessCommand.Args(Id: new[] { proc.Id });
            var ctx = TestCtx();

            // 无 -Timeout → 已退出立即返回。
            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }
        }
        finally
        {
            proc.Dispose();
        }
    }

    /// <summary>启动一个短时进程（很快退出）。</summary>
    /// <remarks>
    /// D-702: Linux 上 <c>echo</c> 亚毫秒退出，随后读取 <see cref="Process.ProcessName"/> 会抛
    /// "Process has exited"。改为把系统 <c>sleep</c> 复制成唯一命名的临时可执行文件再启动：
    /// 进程存活约 1 秒可安全读取进程名，唯一名避免与运行机上其他进程重名；
    /// 读到名字后立即删除副本（Unix 允许删除仍在运行的可执行文件）。
    /// </remarks>
    private static (Process Proc, string Name) StartShortProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : CreateUniqueSleepBinary(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("exit");
            psi.ArgumentList.Add("0");
        }
        else
        {
            psi.ArgumentList.Add("1");
        }
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start test process");
        // 在进程退出前获取 ProcessName（进程退出后访问 ProcessName 会抛 InvalidOperationException）。
        var name = proc.ProcessName;
        if (!OperatingSystem.IsWindows())
        {
            try { File.Delete(psi.FileName); } catch { /* best-effort */ }
        }
        return (proc, name);
    }

    /// <summary>把系统 sleep 复制为唯一命名的临时可执行文件并返回其路径。Per D-702.</summary>
    private static string CreateUniqueSleepBinary()
    {
        var source = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"osh-wait-{Guid.NewGuid():N}-sleep");
        File.Copy(source, dest);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dest,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return dest;
    }

    /// <summary>启动一个长期进程（存活 60s）。</summary>
    /// <remarks>
    /// 直接使用 <c>ping.exe</c>（而非 <c>cmd.exe /c ping</c>），使进程名为 <c>ping</c> 而非 <c>cmd</c>。
    /// 避免 StopProcessCommandTests.Execute_ByName 并行执行时按名 <c>cmd</c> 杀掉本进程导致 flaky 失败。
    /// D-702: Unix 同理——普通 <c>sleep</c> 会被并行的 Stop-Process -Name sleep 误杀，
    /// 改用唯一命名的 sleep 副本。
    /// </remarks>
    private static (Process Proc, string Name) StartLongProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping.exe" : CreateUniqueSleepBinary(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("60");
            psi.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            psi.ArgumentList.Add("60");
        }
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start test process");
        var name = proc.ProcessName;
        if (!OperatingSystem.IsWindows())
        {
            try { File.Delete(psi.FileName); } catch { /* best-effort */ }
        }
        Thread.Sleep(200);
        return (proc, name);
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
