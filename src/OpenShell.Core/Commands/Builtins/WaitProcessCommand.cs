using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Wait-Process</c> 命令：等待进程退出。Per ADR-0048 §7.4.
/// <para>
/// 阻塞当前管道直到目标进程退出。<c>-Timeout</c> 超时（秒），超时抛 <see cref="TimeoutException"/>。
/// </para>
/// </summary>
[Verb("Wait", Noun = "Process", Aliases = ["wait"])]
[Description("Waits for a process to exit.")]
public sealed class WaitProcessCommand : ICommand<WaitProcessCommand.Args>
{
    /// <summary>Arguments for <c>Wait-Process</c>.</summary>
    /// <param name="Name">进程名。</param>
    /// <param name="Id">进程 ID。</param>
    /// <param name="Timeout">超时（秒），null 表示无限等待。</param>
    public record Args(
        [property: Parameter] string[]? Name = null,
        [property: Parameter] int[]? Id = null,
        [property: Parameter] int? Timeout = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.Id is null && args.Name is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Either -Id or -Name must be specified.",
                Operation = "wait-process",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var targets = ResolveTargets(args);

        foreach (var proc in targets)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (args.Timeout is { } timeoutSec)
                {
                    // D-702: Unix 上对 GetProcessById 打开的外部进程，
                    // WaitForExitAsync / Exited 事件均不可靠（抛 InvalidOperationException 被吞）。
                    // 统一用 HasExited 轮询，跨平台一致。
                    var pid = proc.Id;
                    string procName;
                    try { procName = proc.ProcessName; } catch { procName = "?"; }

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                    var exited = await WaitForExitPollingAsync(proc, timeoutCts.Token).ConfigureAwait(false);
                    if (!exited)
                    {
                        ct.ThrowIfCancellationRequested();
                        ctx.Errors?.Write(new ErrorRecord
                        {
                            Category = ErrorCategory.OperationTimeout,
                            Message = $"Process '{procName}' (pid {pid}) did not exit within {timeoutSec} seconds.",
                            Operation = "wait-process",
                            Phase = ErrorPhase.Operation,
                        });
                    }
                }
                else
                {
                    await WaitForExitPollingAsync(proc, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                // 进程已退出或无法访问，忽略。
            }
            finally
            {
                proc.Dispose();
            }
        }

        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// 基于 <see cref="Process.HasExited"/> 的轮询等待。Per D-702.
    /// Unix 下对 <c>Process.GetProcessById</c> 打开的外部进程，<c>WaitForExitAsync</c>
    /// 与 <c>Exited</c> 事件均不可靠（抛 <see cref="InvalidOperationException"/>）；
    /// 轮询跨平台一致。返回是否在取消前退出。
    /// </summary>
    private static async Task<bool> WaitForExitPollingAsync(Process proc, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool exited;
            try
            {
                exited = proc.HasExited;
            }
            catch (InvalidOperationException)
            {
                // 进程句柄已失效（视为已退出/不可访问）。
                return true;
            }
            if (exited) return true;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return proc.HasExited;
            }
        }
        return proc.HasExited;
    }

    private static List<Process> ResolveTargets(Args args)
    {
        var result = new List<Process>();

        if (args.Id is { Length: > 0 } ids)
        {
            foreach (var id in ids)
            {
                try
                {
                    result.Add(Process.GetProcessById(id));
                }
                catch (ArgumentException)
                {
                    // 进程不存在，跳过。
                }
            }
        }

        if (args.Name is { Length: > 0 } names)
        {
            foreach (var name in names)
            {
                try
                {
                    result.AddRange(Process.GetProcessesByName(name));
                }
                catch
                {
                    // 忽略
                }
            }
        }

        return result;
    }
}
