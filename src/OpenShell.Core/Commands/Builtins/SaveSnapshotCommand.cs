using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Sessions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Save-Snapshot</c> 命令。Per ADR-0034 §8.
/// 将当前会话状态保存为命名快照, 含位置 / 历史 / tabs, 不含操作日志 / 历史。
/// </summary>
[Verb("Save", Noun = "Snapshot", Aliases = ["save-snapshot"])]
[Description("Saves the current session state as a named snapshot.")]
[Help(
    Synopsis = "Saves the current session state (location, history, tabs) as a named snapshot.",
    Examples = new[]
    {
        "save-snapshot -Name before-refactor    # save snapshot 'before-refactor'",
    },
    RelatedLinks = new[] { "restore-snapshot", "clear-session" })]
public sealed class SaveSnapshotCommand : ICommand<SaveSnapshotCommand.Args>
{
    /// <param name="Name">快照名称 (用作文件名, 不含扩展名)。</param>
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
                Operation = "save-snapshot",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        try
        {
            await sessions.SaveSnapshotAsync(args.Name, ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync($"Snapshot '{args.Name}' saved.", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Failed to save snapshot '{args.Name}': {ex.Message}",
                Operation = "save-snapshot",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
        }

        yield break;
    }
}
