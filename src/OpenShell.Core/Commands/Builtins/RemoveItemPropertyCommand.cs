using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Remove-ItemProperty</c> 命令。删除 item 的属性。Per ADR-0018 §8 + ADR-0023 M4.
/// <para>
/// 通过 <see cref="IPropertyWriterProvider.RemovePropertyAsync"/> 删除属性。
/// Registry: 删除 value; 幂等 (value 不存在不报错)。
/// </para>
/// </summary>
[Verb("Remove", Noun = "ItemProperty", Aliases = ["rp"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]
[Description("Removes a property from an item.")]
public sealed class RemoveItemPropertyCommand : ICommand<RemoveItemPropertyCommand.Args>
{
    /// <summary>Arguments for <c>Remove-ItemProperty</c>.</summary>
    /// <param name="Path">目标 item 路径。</param>
    /// <param name="Name">要删除的属性名。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        if (!ctx.ShouldProcess($"{path.Display} ! {args.Name}", "Remove-ItemProperty", ConfirmImpact.High))
            yield break;

        var writer = ctx.Providers.ResolveCapability<IPropertyWriterProvider>(path);
        if (writer is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support removing properties.",
                TargetPath = path,
                Operation = "remove-itemproperty",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        try
        {
            await writer.RemovePropertyAsync(path, args.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = $"Access denied removing property '{args.Name}' from '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "remove-itemproperty",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Failed to remove property '{args.Name}' from '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "remove-itemproperty",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Removed property '{args.Name}' from '{path.Display}'.", ct).ConfigureAwait(false);
    }

    private static ItemPath ResolvePath(ItemPath path, CommandContext ctx)
    {
        if (path.Provider != "fs" || path.IsRooted)
            return path;
        return ctx.CurrentLocation.Provider == "fs"
            ? ctx.CurrentLocation.Combine(path.InternalPath)
            : new ItemPath { Provider = "fs", InternalPath = path.InternalPath };
    }
}
