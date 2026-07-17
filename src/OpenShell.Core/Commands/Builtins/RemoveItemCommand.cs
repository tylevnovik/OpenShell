using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Remove-Item</c> command. Per ADR-0023 M1. Delegates to
/// <see cref="IOperationEngine.DeleteAsync"/> to remove an item. By default the
/// item is sent to the recycle bin (trash); pass <c>-Force</c> to physically
/// delete it.
/// </summary>
[Verb("Remove", Noun = "Item", Aliases = ["rm", "del", "ri"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]
[Description("Removes an item, by default to the recycle bin.")]
public sealed class RemoveItemCommand : ICommand<RemoveItemCommand.Args>
{
    /// <summary>Arguments for <c>Remove-Item</c>.</summary>
    /// <param name="Path">Path of the item to remove.</param>
    /// <param name="Recurse">Recurse into subdirectories.</param>
    /// <param name="Force">Physically delete instead of sending to trash.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Aliases = new[] { "-r" })] bool Recurse = false,
        [property: Parameter(Aliases = new[] { "-f" })] bool Force = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var engine = ctx.Operations;
        if (engine is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Operation engine is not available in this context.",
                Operation = "remove-item",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var path = ResolvePath(args.Path, ctx);

        // ADR-0049 §7: gate the destructive delete. -Recurse -Force always raises impact to High;
        // plain Remove-Item is also High here per ADR-0049 §5 (rm is the canonical destructive op).
        var action = args.Force ? "physically remove" : "remove (to recycle bin)";
        if (!ctx.ShouldProcess(path.Display, action, ConfirmImpact.High)) yield break;

        var options = new DeleteOptions { Recurse = args.Recurse, UseTrash = !args.Force };

        var result = await engine.DeleteAsync(path, options, null, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Removed {result.ItemsAffected} items.", ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var err in result.Errors)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.OperationFailed,
                    Message = err.Message,
                    TargetPath = err.Path,
                    Operation = "remove-item",
                    Phase = ErrorPhase.Operation,
                    Exception = err.Exception,
                });
            }
        }

        yield break;
    }

    private static ItemPath ResolvePath(ItemPath path, CommandContext ctx)
    {
        // 非 fs provider 的路径：不与 fs CurrentLocation 组合（跨 provider 路径不互通）。
        if (path.Provider != "fs" || path.IsRooted)
            return path;
        // fs 相对路径：在 fs CurrentLocation 下组合。
        return ctx.CurrentLocation.Provider == "fs"
            ? ctx.CurrentLocation.Combine(path.InternalPath)
            : new ItemPath { Provider = "fs", InternalPath = path.InternalPath };
    }
}
