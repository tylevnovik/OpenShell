using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Locations;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Push-Location</c> command. Per ADR-0023 M1 / ADR-0048 §3.6. Pushes the
/// current working location onto the host's <see cref="ILocationStack"/> singleton
/// (resolved from <see cref="IHost.Services"/>) and switches to the specified
/// location. If <c>-Path</c> is omitted, only the current location is pushed.
/// </summary>
[Verb("Push", Noun = "Location", Aliases = ["pushd", "push"])]
[Description("Pushes the current location onto a stack and switches to a new location.")]
public sealed class PushLocationCommand : ICommand<PushLocationCommand.Args>
{
    /// <summary>Arguments for <c>Push-Location</c>.</summary>
    /// <param name="Path">Target path. Bare relative paths resolve against the current location. Null selects the provider root.</param>
    public record Args(
        [property: Parameter(Position = 0)] ItemPath? Path = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stack = LocationStackResolver.Resolve(ctx);
        var current = ctx.Host.CurrentLocation;
        stack.Push(current);

        var path = args.Path ?? ItemPath.Root(current.Provider);
        path = ResolvePath(path, ctx);

        var itemProvider = ctx.Providers.ResolveCapability<IItemProvider>(path);
        if (itemProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support item retrieval.",
                TargetPath = path,
                Operation = "push-location",
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
                Operation = "push-location",
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

/// <summary>
/// Resolves the <see cref="ILocationStack"/> singleton from <see cref="IHost.Services"/>.
/// Shared between <c>Push-Location</c> and <c>Pop-Location</c>. Per ADR-0048 §3.6.
/// </summary>
internal static class LocationStackResolver
{
    /// <summary>
    /// Resolve the <see cref="ILocationStack"/>. Returns a process-wide fallback when the host
    /// does not expose DI services (legacy hosts or minimal test setups).
    /// </summary>
    public static ILocationStack Resolve(CommandContext ctx)
    {
        try
        {
            if (ctx.Host.Services.GetService(typeof(ILocationStack)) is ILocationStack stack)
                return stack;
        }
        catch (NotSupportedException)
        {
            // Hosts that do not expose DI — fall through to fallback.
        }
        return FallbackLocationStack.Instance;
    }

    private sealed class FallbackLocationStack : ILocationStack
    {
        private readonly Stack<ItemPath> _inner = new();
        public static FallbackLocationStack Instance { get; } = new();
        public void Push(ItemPath location) => _inner.Push(location);
        public ItemPath Pop()
            => _inner.Count == 0
                ? throw new InvalidOperationException("The location stack is empty.")
                : _inner.Pop();
        public bool TryPop(out ItemPath location) => _inner.TryPop(out location);
        public int Count => _inner.Count;
    }
}
