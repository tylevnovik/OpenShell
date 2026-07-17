using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Remoting;
using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Remove-PSSession</c> 命令。Per ADR-0059 §6.
/// 关闭并移除远程会话 (释放 ssh 子进程)。
/// </summary>
[Verb("Remove", Noun = "PSSession", Aliases = ["rsn"])]
[Description("Closes and removes a remote session.")]
[Help(
    Synopsis = "Closes and removes a PSSession (Remove-PSSession).",
    Examples = new[]
    {
        "remove-pssession -Id 1           # close session 1",
        "remove-pssession -Session $s     # close by session object",
    },
    RelatedLinks = new[] { "new-pssession", "get-pssession" })]
public sealed class RemovePSSessionCommand : ICommand<RemovePSSessionCommand.Args>
{
    /// <summary>Arguments for <c>Remove-PSSession</c>.</summary>
    /// <param name="Id">要移除的会话 Id。与 <paramref name="Session"/> 互斥。</param>
    /// <param name="Session">要移除的会话对象。与 <paramref name="Id"/> 互斥。</param>
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
                Operation = "remove-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 解析目标会话 Id。
        int? targetId = args.Session?.Id ?? args.Id;
        if (targetId is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Remove-PSSession requires -Id or -Session.",
                Operation = "remove-pssession",
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
                Operation = "remove-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 若是当前活跃会话, 清除活跃标记。
        if (manager.ActiveSessionId == targetId.Value)
            manager.ActiveSessionId = null;

        manager.Remove(targetId.Value);
        await ctx.Host.WriteOutputLineAsync(
            $"PSSession {targetId.Value} closed.", ct).ConfigureAwait(false);
        yield break;
    }
}
