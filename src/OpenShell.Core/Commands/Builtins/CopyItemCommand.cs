using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Copy-Item</c> command. Per ADR-0023 M1. Delegates to
/// <see cref="IOperationEngine.CopyAsync"/> to copy an item from source to
/// destination, optionally recursing and/or overwriting existing items.
/// </summary>
[Verb("Copy", Noun = "Item", Aliases = ["cp", "cpi", "copy"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Low)]
[Description("Copies an item from a source path to a destination path.")]
public sealed class CopyItemCommand : ICommand<CopyItemCommand.Args>
{
    /// <summary>Arguments for <c>Copy-Item</c>.</summary>
    /// <param name="Source">Source item path.</param>
    /// <param name="Destination">Destination path.</param>
    /// <param name="Recurse">Recurse into subdirectories.</param>
    /// <param name="Force">Overwrite an existing destination item.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Source,
        [property: Parameter(Position = 1, Mandatory = true)] ItemPath Destination,
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
                Operation = "copy-item",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var source = ResolvePath(args.Source, ctx);
        var destination = ResolvePath(args.Destination, ctx);

        // ADR-0049 §7: gate the copy. Overwriting an existing target raises impact to Medium.
        var impact = args.Force ? ConfirmImpact.Medium : ConfirmImpact.Low;
        if (!ctx.ShouldProcess($"{source.Display} → {destination.Display}", "Copy", impact)) yield break;

        var options = new CopyOptions { Recurse = args.Recurse, Force = args.Force };

        var result = await engine.CopyAsync(source, destination, options, null, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Copied {result.ItemsAffected} items ({result.BytesTransferred} bytes).", ct)
                .ConfigureAwait(false);
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
                    Operation = "copy-item",
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
