using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Remoting;
using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Exit-PSSession</c> 命令。Per ADR-0059 §6.
/// 退出交互式远程会话, 恢复本地 REPL。
/// </summary>
[Verb("Exit", Noun = "PSSession", Aliases = ["exsn"])]
[Description("Exits an interactive remote session.")]
[Help(
    Synopsis = "Exits the current interactive PSSession (Exit-PSSession).",
    Examples = new[] { "exit-pssession    # return to local REPL" },
    RelatedLinks = new[] { "enter-pssession" })]
public sealed class ExitPSSessionCommand : ICommand<ExitPSSessionCommand.Args>
{
    /// <summary>Arguments for <c>Exit-PSSession</c> (无参数)。</summary>
    public record Args();

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var manager = ctx.Host.Services.GetService(typeof(PSSessionManager)) as PSSessionManager;
        if (manager is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Remoting service is not available in this context.",
                Operation = "exit-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (manager.ActiveSessionId is int id)
        {
            manager.ActiveSessionId = null;
            await ctx.Host.WriteOutputLineAsync(
                $"Exited PSSession {id}. Returned to local session.", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync(
                "Not in a remote session.", ct).ConfigureAwait(false);
        }
        yield break;
    }
}
