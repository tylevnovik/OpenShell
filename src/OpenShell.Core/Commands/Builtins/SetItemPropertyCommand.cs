using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Set-ItemProperty</c> 命令。设置 item 的属性值。Per ADR-0018 §8 + ADR-0023 M4.
/// <para>
/// 通过 <see cref="IPropertyWriterProvider"/> 写入属性。Registry: 设置 value;
/// 未来 FS: 设置文件属性 (ReadOnly/Hidden 等)。
/// 声明 <c>SupportsShouldProcess</c> (per ADR-0049).
/// </para>
/// </summary>
[Verb("Set", Noun = "ItemProperty", Aliases = ["sp"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Sets the value of a property of an item.")]
public sealed class SetItemPropertyCommand : ICommand<SetItemPropertyCommand.Args>
{
    /// <summary>Arguments for <c>Set-ItemProperty</c>.</summary>
    /// <param name="Path">目标 item 路径。</param>
    /// <param name="Name">属性名 (Registry: value name; 空字符串表示 default value)。</param>
    /// <param name="Value">属性值。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string Name,
        [property: Parameter(Position = 2)] object? Value = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        if (!ctx.ShouldProcess($"{path.Display} -> {args.Name}={args.Value}", "Set-ItemProperty", ConfirmImpact.Medium))
            yield break;

        var writer = ctx.Providers.ResolveCapability<IPropertyWriterProvider>(path);
        if (writer is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support writing properties.",
                TargetPath = path,
                Operation = "set-itemproperty",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        try
        {
            await writer.SetPropertyAsync(path, args.Name, args.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = $"Access denied writing property '{args.Name}' to '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "set-itemproperty",
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
                Message = $"Failed to set property '{args.Name}' on '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "set-itemproperty",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Set property '{args.Name}' on '{path.Display}'.", ct).ConfigureAwait(false);
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
