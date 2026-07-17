using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Wait-Job</c> 命令：等待后台任务完成。Per ADR-0044 §11.
/// <para>
/// 阻塞当前管道直到指定任务 (或全部活动任务) 进入终态 (Completed / Failed / Cancelled)。
/// <c>-Timeout</c> 超时 (秒), 超时后提示未完成的任务。
/// </para>
/// </summary>
[Verb("Wait", Noun = "Job", Aliases = ["wj", "wait-job"])]
[Description("Waits for background jobs to complete.")]
public sealed class WaitJobCommand : ICommand<WaitJobCommand.Args>
{
    /// <summary>Arguments for <c>Wait-Job</c>.</summary>
    /// <param name="Id">要等待的任务 ID (可多个); 省略则等待全部活动任务。</param>
    /// <param name="Timeout">超时 (秒), null 表示无限等待。</param>
    /// <param name="Any">若为 true, 任一指定任务完成即返回 (默认 false: 等待全部)。</param>
    public record Args(
        [property: Parameter] Guid[]? Id = null,
        [property: Parameter] int? Timeout = null,
        [property: Parameter] bool Any = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var taskCenter = ctx.Host.Services.GetService<ITaskCenter>();
        if (taskCenter is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Task center is not available; background jobs not supported in this context.",
                Operation = "wait-job",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 收集目标任务: 按 Id 过滤, 或全部活动任务。
        var targets = taskCenter.ActiveTasks.ToList();
        if (args.Id is { Length: > 0 } ids)
        {
            var idSet = new HashSet<Guid>(ids);
            targets = targets.Where(t => idSet.Contains(t.TaskId)).ToList();
            // 若 Id 指定但不在 ActiveTasks 中, 查 RecentCompleted (可能已完成)。
            foreach (var completed in taskCenter.RecentCompleted)
            {
                if (idSet.Contains(completed.TaskId) && !targets.Any(t => t.TaskId == completed.TaskId))
                {
                    targets.Add(completed);
                }
            }
        }

        if (targets.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("(no matching jobs to wait for)", ct).ConfigureAwait(false);
            yield break;
        }

        // 构造等待任务: 为每个目标创建 TCS, StateChanged 终态时完成。
        var pending = targets.Where(t => !IsTerminal(t.State)).ToList();
        if (pending.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("All specified jobs already completed.", ct).ConfigureAwait(false);
            yield break;
        }

        var tcsList = pending.Select(t => CreateCompletionSource(t, ct)).ToList();
        // allTask: Task<bool[]>; anyTask: Task<Task<bool>>. 两者类型不同,
        // 统一向上提升为 Task 用于 WhenAny(waitTask, delayTask) 调度。结果状态通过 handle.State 读取。
        Task allTask = Task.WhenAll(tcsList.Select(x => x.Tcs.Task));
        Task anyTask = Task.WhenAny(tcsList.Select(x => x.Tcs.Task));

        var waitTask = args.Any ? anyTask : allTask;

        TimeSpan? timeout = args.Timeout is { } sec ? TimeSpan.FromSeconds(sec) : null;
        var delayTask = timeout is { } ts ? Task.Delay(ts, ct) : Task.Delay(Timeout.Infinite, ct);

        var winner = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

        // 报告结果
        foreach (var (handle, tcs) in tcsList)
        {
            var state = handle.State;
            var idShort = handle.TaskId.ToString().Substring(0, 8);
            await ctx.Host.WriteOutputLineAsync(
                $"  {idShort}  {state,-10}  {handle.DisplayLabel}", ct).ConfigureAwait(false);
        }

        if (winner == delayTask && timeout is not null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationTimeout,
                Message = $"Wait-Job timed out after {args.Timeout} seconds; {pending.Count(t => !IsTerminal(t.State))} job(s) still running.",
                Operation = "wait-job",
                Phase = ErrorPhase.Operation,
            });
        }

        yield break;
    }

    /// <summary>判断任务是否处于终态。</summary>
    private static bool IsTerminal(TaskState state) =>
        state is TaskState.Completed or TaskState.Failed or TaskState.Cancelled;

    /// <summary>为任务句柄创建完成 TCS: StateChanged 进入终态时 TrySetResult。</summary>
    private static (ITaskHandle Handle, TaskCompletionSource<bool> Tcs) CreateCompletionSource(
        ITaskHandle handle, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsTerminal(handle.State))
        {
            tcs.TrySetResult(true);
            return (handle, tcs);
        }

        handle.StateChanged += (_, state) =>
        {
            if (IsTerminal(state)) tcs.TrySetResult(true);
        };

        // 取消令牌触发时释放等待者。
        ct.Register(() => tcs.TrySetCanceled(ct));

        return (handle, tcs);
    }
}
