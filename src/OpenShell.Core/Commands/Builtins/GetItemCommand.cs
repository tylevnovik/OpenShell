using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Item</c> command. Per ADR-0023 M1. Resolves a single item at
/// the specified path via the provider's <see cref="IItemProvider"/> capability.
/// Emits an <see cref="ErrorRecord"/> of category <c>ItemNotFound</c> when the
/// path does not resolve.
/// </summary>
[Verb("Get", Noun = "Item", Aliases = ["gi"])]
[Description("Gets a single item at the specified path.")]
public sealed class GetItemCommand : ICommand<GetItemCommand.Args>, OpenShell.Pipeline.IPipelineSource
{
    /// <summary>Arguments for <c>Get-Item</c>.</summary>
    /// <param name="Path">Path of the item to retrieve. Bare relative paths resolve against the current location.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        var itemProvider = ctx.Providers.ResolveCapability<IItemProvider>(path);
        if (itemProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support item retrieval.",
                TargetPath = path,
                Operation = "get-item",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        var item = await itemProvider.GetItemAsync(path, ct).ConfigureAwait(false);
        if (item is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Item not found: {path.Display}",
                TargetPath = path,
                Operation = "get-item",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        yield return item;
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
