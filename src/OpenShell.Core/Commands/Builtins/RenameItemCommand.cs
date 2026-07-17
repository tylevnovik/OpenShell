using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Rename-Item</c> command. Per ADR-0023 M1. Delegates to
/// <see cref="IOperationEngine.RenameAsync"/> to rename an item in place.
/// </summary>
[Verb("Rename", Noun = "Item", Aliases = ["rn", "rni"])]
[Description("Renames an item in place.")]
public sealed class RenameItemCommand : ICommand<RenameItemCommand.Args>
{
    /// <summary>Arguments for <c>Rename-Item</c>.</summary>
    /// <param name="Path">Path of the item to rename.</param>
    /// <param name="NewName">New name (last path segment) for the item.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string NewName);

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
                Operation = "rename-item",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (string.IsNullOrWhiteSpace(args.NewName))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "NewName is required.",
                Operation = "rename-item",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var path = ResolvePath(args.Path, ctx);

        var result = await engine.RenameAsync(path, args.NewName, null, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Renamed to '{args.NewName}'.", ct).ConfigureAwait(false);
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
                    Operation = "rename-item",
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
