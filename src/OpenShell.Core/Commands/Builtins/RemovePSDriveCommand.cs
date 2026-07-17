using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Remove-PSDrive</c> command. Per ADR-0023 M1. Unmounts a
/// virtual drive previously created by <c>New-PSDrive</c>. Physical drives
/// reported by <see cref="IDriveProvider"/> cannot be removed.
/// </summary>
[Verb("Remove", Noun = "PSDrive", Aliases = ["rdr", "unmount"])]
[Description("Unmounts a virtual drive previously created by New-PSDrive.")]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Help(
    Synopsis = "Unmounts a virtual drive previously created by New-PSDrive.",
    Examples = new[]
    {
        "remove-psdrive -Name tmp                  # unmount tmp",
        "unmount work                              # alias form",
    },
    RelatedLinks = new[] { "get-psdrive", "new-psdrive" })]
public sealed class RemovePSDriveCommand : ICommand<RemovePSDriveCommand.Args>
{
    /// <summary>Arguments for <c>Remove-PSDrive</c>.</summary>
    /// <param name="Name">Name of the virtual drive to unmount.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var drives = ctx.Drives;
        if (drives is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Drive registry is not available in this context.",
                Operation = "remove-psdrive",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var name = args.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Drive name is required.",
                Operation = "remove-psdrive",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var existing = drives.Find(name);
        if (existing is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"No virtual drive named '{name}' is mounted.",
                Operation = "remove-psdrive",
                Phase = ErrorPhase.Operation,
                Suggestion = "try 'get-psdrive' to list mounted drives.",
            });
            yield break;
        }

        // ADR-0049 §7: gate the destructive unmount.
        if (!ctx.ShouldProcess($"drive '{name}' (root: {existing.Root.Display})", "Unmount", ConfirmImpact.Medium)) yield break;

        drives.Unmount(name);
        await ctx.Host.WriteOutputLineAsync(
            $"Unmounted drive '{name}' (was: {existing.Root.Display})", ct).ConfigureAwait(false);

        yield break;
    }
}
