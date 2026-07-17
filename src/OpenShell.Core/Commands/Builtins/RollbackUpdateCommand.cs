using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Updates;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Rollback-Update</c> 命令。Per ADR-0037 §7 / §11.
/// 查找当前 exe 旁的 <c>.old</c> 备份并恢复之。
/// </summary>
[Verb("Rollback", Noun = "Update", Aliases = ["rollback-update"])]
[Description("Restores the previous OpenShell version from the .old backup.")]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]
[Help(
    Synopsis = "Rolls back to the previous OpenShell version using the .old backup file.",
    Examples = new[]
    {
        "rollback-update           # restore previous version",
    },
    RelatedLinks = new[] { "check-update", "update-openshell" })]
public sealed class RollbackUpdateCommand : ICommand<RollbackUpdateCommand.Args>
{
    /// <summary>Arguments for <c>Rollback-Update</c>.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var updateService = ctx.Host.Services.GetService(typeof(IUpdateService)) as IUpdateService;
        if (updateService is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Update service is not available in this context.",
                Operation = "rollback-update",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (updateService is not GitHubReleasesUpdateService gh)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Rollback is only supported by the GitHub Releases update service.",
                Operation = "rollback-update",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // ADR-0049 §7: gate the destructive rollback.
        if (!ctx.ShouldProcess("current OpenShell installation", "Rollback update", ConfirmImpact.High)) yield break;

        bool ok;
        try
        {
            ok = gh.Rollback();
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.IOError,
                Message = $"Failed to rollback: {ex.Message}",
                Operation = "rollback-update",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        if (!ok)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = "No previous version found to rollback. (Looking for an .old file next to the current executable.)",
                Operation = "rollback-update",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            "Rollback complete. Restart OpenShell for the change to take effect.", ct).ConfigureAwait(false);

        yield break;
    }
}
