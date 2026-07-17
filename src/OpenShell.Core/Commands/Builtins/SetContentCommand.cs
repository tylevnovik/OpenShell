using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Set-Content</c> command. Per ADR-0023 M1. Writes the supplied
/// string value to an item via the provider's <see cref="IContentWriterProvider"/>,
/// overwriting any existing content.
/// </summary>
[Verb("Set", Noun = "Content", Aliases = ["sc", "write"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Low)]
[Description("Writes text content to an item, overwriting existing content.")]
public sealed class SetContentCommand : ICommand<SetContentCommand.Args>
{
    /// <summary>Arguments for <c>Set-Content</c>.</summary>
    /// <param name="Path">Path of the item to write.</param>
    /// <param name="Value">Text content to write.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string Value);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.Value is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Value is required.",
                Operation = "set-content",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var path = ResolvePath(args.Path, ctx);

        // ADR-0049 §7: gate the destructive write (overwrites existing content).
        if (!ctx.ShouldProcess(path.Display, "Set content", ConfirmImpact.Low)) yield break;

        var writer = ctx.Providers.ResolveCapability<IContentWriterProvider>(path);
        if (writer is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support writing content.",
                TargetPath = path,
                Operation = "set-content",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        await using var stream = await writer.OpenWriteAsync(path, ct).ConfigureAwait(false);
        await using var sw = new StreamWriter(stream);
        await sw.WriteAsync(args.Value.AsMemory(), ct).ConfigureAwait(false);
        await sw.FlushAsync(ct).ConfigureAwait(false);

        await ctx.Host.WriteOutputLineAsync(
            $"Wrote {args.Value.Length} chars.", ct).ConfigureAwait(false);

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
