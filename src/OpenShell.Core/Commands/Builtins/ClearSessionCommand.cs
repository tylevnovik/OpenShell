using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Sessions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Clear-Session</c> 命令。Per ADR-0034 §13.
/// 清除指定会话的所有持久化数据 (session JSON + lock 文件)。
/// 默认清除 "default" 会话, 可用 <c>-Name</c> 指定其他会话。
/// </summary>
[Verb("Clear", Noun = "Session", Aliases = ["clear-session"])]
[Description("Clears persisted data for a session (state JSON + lock file).")]
[Help(
    Synopsis = "Clears all persisted data for the specified session. Does not affect snapshots.",
    Examples = new[]
    {
        "clear-session                  # clear the 'default' session",
        "clear-session -Name work        # clear the 'work' session",
    },
    RelatedLinks = new[] { "save-snapshot", "restore-snapshot" })]
public sealed class ClearSessionCommand : ICommand<ClearSessionCommand.Args>
{
    /// <param name="Name">会话名称 (默认 "default")。</param>
    public record Args(
        [property: Parameter(Position = 0)] string Name = "default");

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
                Operation = "clear-session",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        try
        {
            await sessions.ClearSessionAsync(args.Name, ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync(
                $"Session '{args.Name}' cleared.", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Failed to clear session '{args.Name}': {ex.Message}",
                Operation = "clear-session",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
        }

        yield break;
    }
}
