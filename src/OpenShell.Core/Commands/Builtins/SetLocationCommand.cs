using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Set-Location</c> command. Per ADR-0023 M1. Changes the host's
/// current working location. When <c>Path</c> is null, switches to the provider
/// root. The target location is validated via <see cref="IItemProvider"/> before
/// the host state is updated.
/// </summary>
[Verb("Set", Noun = "Location", Aliases = ["cd", "chdir", "sl"])]
[Description("Changes the current working location.")]
public sealed class SetLocationCommand : ICommand<SetLocationCommand.Args>
{
    /// <summary>Arguments for <c>Set-Location</c>.</summary>
    /// <param name="Path">Target path. Bare relative paths resolve against the current location. Null selects the provider root.</param>
    public record Args(
        [property: Parameter(Position = 0)] ItemPath? Path = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = args.Path ?? ItemPath.Root(ctx.CurrentLocation.Provider);
        path = ResolvePath(path, ctx);

        var itemProvider = ctx.Providers.ResolveCapability<IItemProvider>(path);
        if (itemProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support item retrieval.",
                TargetPath = path,
                Operation = "set-location",
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
                Message = $"Location not found: {path.Display}",
                TargetPath = path,
                Operation = "set-location",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        ctx.Host.CurrentLocation = path;
        await ctx.Host.WriteOutputLineAsync(path.Display, ct).ConfigureAwait(false);

        yield break;
    }

    private static ItemPath ResolvePath(ItemPath path, CommandContext ctx)
    {
        // 非 fs provider 的路径：不与 fs CurrentLocation 组合（跨 provider 路径不互通）。
        if (path.Provider != "fs" || path.IsRooted)
            return path;
        // fs 相对路径：在 fs CurrentLocation 下组合。
        var combined = ctx.CurrentLocation.Provider == "fs"
            ? ctx.CurrentLocation.Combine(path.InternalPath)
            : new ItemPath { Provider = "fs", InternalPath = path.InternalPath };
        // 规范化 . 和 .. 段，避免 CurrentLocation 存储 "C:/Users/foo/.." 这样的未规范化路径。
        var navProvider = ctx.Providers.ResolveCapability<INavigationProvider>(combined);
        return navProvider?.NormalizePath(combined) ?? combined;
    }
}
