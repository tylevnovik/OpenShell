using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Remoting;
using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Enter-PSSession</c> 命令。Per ADR-0059 §6.
/// 进入交互式远程会话 (后续 REPL 输入转发到远端)。
/// 简化实现: 仅设置 ActiveSessionId, 由宿主 REPL 检查此字段决定转发行为。
/// </summary>
[Verb("Enter", Noun = "PSSession", Aliases = ["etsn"])]
[Description("Enters an interactive remote session.")]
[Help(
    Synopsis = "Enters an interactive PSSession (Enter-PSSession).",
    Examples = new[]
    {
        "enter-pssession -Id 1     # enter session 1",
        "enter-pssession -Session $s",
    },
    RelatedLinks = new[] { "exit-pssession", "new-pssession" })]
public sealed class EnterPSSessionCommand : ICommand<EnterPSSessionCommand.Args>
{
    /// <summary>Arguments for <c>Enter-PSSession</c>.</summary>
    /// <param name="Id">要进入的会话 Id。与 <paramref name="Session"/> 互斥。</param>
    /// <param name="Session">要进入的会话对象。与 <paramref name="Id"/> 互斥。</param>
    public record Args(
        [property: Parameter] int? Id = null,
        [property: Parameter] IPSSession? Session = null);

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
                Operation = "enter-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        int? targetId = args.Session?.Id ?? args.Id;
        if (targetId is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Enter-PSSession requires -Id or -Session.",
                Operation = "enter-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var session = manager.Get(targetId.Value);
        if (session is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"PSSession with Id {targetId.Value} not found.",
                Operation = "enter-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (session.State != PSSessionState.Opened)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"PSSession {targetId.Value} is not opened (state={session.State}).",
                Operation = "enter-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        manager.ActiveSessionId = targetId.Value;
        await ctx.Host.WriteOutputLineAsync(
            $"Entered PSSession {targetId.Value} ({session.ComputerName}). Type 'exit-pssession' to return.",
            ct).ConfigureAwait(false);
        yield break;
    }
}
