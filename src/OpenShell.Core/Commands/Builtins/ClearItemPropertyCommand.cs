using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Clear-ItemProperty</c> 命令。清除 item 的属性值 (置空, 不删除属性名)。Per ADR-0018 §8 + ADR-0023 M4.
/// <para>
/// 通过 <see cref="IPropertyWriterProvider.ClearPropertyAsync"/> 清除属性值。
/// Registry: value 置为空字符串。
/// </para>
/// </summary>
[Verb("Clear", Noun = "ItemProperty", Aliases = ["clp"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Clears the value of a property but does not delete the property.")]
public sealed class ClearItemPropertyCommand : ICommand<ClearItemPropertyCommand.Args>
{
    /// <summary>Arguments for <c>Clear-ItemProperty</c>.</summary>
    /// <param name="Path">目标 item 路径。</param>
    /// <param name="Name">要清除的属性名。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        if (!ctx.ShouldProcess($"{path.Display} -> {args.Name}=(clear)", "Clear-ItemProperty", ConfirmImpact.Medium))
            yield break;

        var writer = ctx.Providers.ResolveCapability<IPropertyWriterProvider>(path);
        if (writer is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support clearing properties.",
                TargetPath = path,
                Operation = "clear-itemproperty",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        try
        {
            await writer.ClearPropertyAsync(path, args.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = $"Access denied clearing property '{args.Name}' on '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "clear-itemproperty",
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
                Message = $"Failed to clear property '{args.Name}' on '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "clear-itemproperty",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Cleared property '{args.Name}' on '{path.Display}'.", ct).ConfigureAwait(false);
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
