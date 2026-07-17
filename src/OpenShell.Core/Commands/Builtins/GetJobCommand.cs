using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-Job</c> 命令：列出当前后台任务 (活动 + 最近完成)。Per ADR-0044 §11.
/// <para>
/// 从 <see cref="ITaskCenter"/> 读取 ActiveTasks + RecentCompleted,
/// 展示 ID / State / Operation / DisplayLabel / Progress。
/// 无 ITaskCenter 时提示降级。
/// </para>
/// </summary>
[Verb("Get", Noun = "Job", Aliases = ["gj", "jobs"])]
[Description("Lists background jobs (active and recently completed).")]
public sealed class GetJobCommand : ICommand<GetJobCommand.Args>
{
    /// <summary>Arguments for <c>Get-Job</c>.</summary>
    /// <param name="Id">按任务 ID 过滤 (可选)。</param>
    /// <param name="State">按状态过滤 (可选: Active / Completed / Any, 默认 Any)。</param>
    public record Args(
        [property: Parameter] Guid[]? Id = null,
        [property: Parameter] string? State = null);

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
                Operation = "get-job",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var filterState = (args.State ?? "Any").Trim();
        var wantActive = !string.Equals(filterState, "Completed", StringComparison.OrdinalIgnoreCase);
        var wantCompleted = !string.Equals(filterState, "Active", StringComparison.OrdinalIgnoreCase);

        var idSet = args.Id is { Length: > 0 } ids
            ? new HashSet<Guid>(ids)
            : null;

        // 表头
        await ctx.Host.WriteOutputLineAsync(
            "  #".PadRight(6) + "Id".PadRight(38) + "State".PadRight(12) + "Operation".PadRight(12) + "Label", ct).ConfigureAwait(false);

        var idx = 1;
        if (wantActive)
        {
            foreach (var t in taskCenter.ActiveTasks)
            {
                ct.ThrowIfCancellationRequested();
                if (idSet is not null && !idSet.Contains(t.TaskId)) continue;
                await WriteJobLineAsync(ctx.Host, idx, t, ct).ConfigureAwait(false);
                idx++;
            }
        }

        if (wantCompleted)
        {
            foreach (var t in taskCenter.RecentCompleted)
            {
                ct.ThrowIfCancellationRequested();
                if (idSet is not null && !idSet.Contains(t.TaskId)) continue;
                await WriteJobLineAsync(ctx.Host, idx, t, ct).ConfigureAwait(false);
                idx++;
            }
        }

        if (idx == 1)
        {
            await ctx.Host.WriteOutputLineAsync("(no jobs)", ct).ConfigureAwait(false);
        }

        yield break;
    }

    private static async Task WriteJobLineAsync(IHost host, int idx, ITaskHandle t, CancellationToken ct)
    {
        var idShort = t.TaskId.ToString().Substring(0, 8);
        var state = t.State.ToString();
        var op = t.Operation.Length > 10 ? t.Operation.Substring(0, 10) : t.Operation;
        await host.WriteOutputLineAsync(
            $"{idx,4}. {idShort,-36} {state,-10} {op,-10} {t.DisplayLabel}", ct).ConfigureAwait(false);
    }
}
