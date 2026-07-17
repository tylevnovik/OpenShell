using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Sessions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Restore-Snapshot</c> 命令。Per ADR-0034 §8.
/// 加载命名快照覆盖当前会话状态, 不影响操作日志与历史。
/// </summary>
[Verb("Restore", Noun = "Snapshot", Aliases = ["restore-snapshot"])]
[Description("Loads a named snapshot, replacing the current session state.")]
[Help(
    Synopsis = "Restores session state from a named snapshot. Does not affect operation log or history.",
    Examples = new[]
    {
        "restore-snapshot -Name before-refactor    # restore snapshot 'before-refactor'",
    },
    RelatedLinks = new[] { "save-snapshot", "clear-session" })]
public sealed class RestoreSnapshotCommand : ICommand<RestoreSnapshotCommand.Args>
{
    /// <param name="Name">快照名称 (不含扩展名)。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sessions = ctx.Host.Services.GetService(typeof(ISessionService)) as ISessionService;
        if (sessions is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Session service is not available in this context.",
                Operation = "restore-snapshot",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        Session? snapshot;
        try
        {
            snapshot = await sessions.LoadSnapshotAsync(args.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Failed to read snapshot '{args.Name}': {ex.Message}",
                Operation = "restore-snapshot",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        if (snapshot is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Snapshot '{args.Name}' does not exist.",
                Operation = "restore-snapshot",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // Per ADR-0034 §8: restore-snapshot 加载快照覆盖当前会话状态, 不影响操作日志。
        // ISessionService.Current 仅 getter, 实际状态应用 (cd 到位置 / 恢复 tabs) 由 host 观察快照后完成;
        // 此命令负责加载并展示快照内容, host 在下次 SaveAsync 时持久化恢复后的状态。
        await ctx.Host.WriteOutputLineAsync(
            $"Snapshot '{args.Name}' loaded (created {snapshot.Created:yyyy-MM-dd HH:mm:ss} UTC).",
            ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  location: {snapshot.State.CurrentLocation.Display}", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  history: {snapshot.State.NavigationHistory.Count} item(s)", ct).ConfigureAwait(false);

        yield break;
    }
}
