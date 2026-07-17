using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.History;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Clear-History</c> command. Per ADR-0020, ADR-0008 §3.
/// 清除全部命令历史 (含持久化文件)。
/// </summary>
[Verb("Clear", Noun = "History", Aliases = ["clh"])]
[Description("Clears all command history.")]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Help(
    Synopsis = "Clears all command history (memory + persisted file).",
    Examples = new[] { "clear-history" },
    RelatedLinks = new[] { "get-history" })]
public sealed class ClearHistoryCommand : ICommand<ClearHistoryCommand.Args>
{
    /// <summary>Arguments for <c>Clear-History</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var history = ctx.Host.Services.GetService(typeof(IHistoryService)) as IHistoryService;
        if (history is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "History service is not available in this context.",
                Operation = "clear-history",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // ADR-0049 §7: gate the destructive clear.
        if (!ctx.ShouldProcess("command history", "Clear", ConfirmImpact.Medium)) yield break;

        history.Clear();
        await ctx.Host.WriteOutputLineAsync("History cleared.", ct).ConfigureAwait(false);

        yield break;
    }
}
