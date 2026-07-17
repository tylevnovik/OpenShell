using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>New-ItemProperty</c> 命令。为 item 创建新属性。Per ADR-0018 §8 + ADR-0023 M4.
/// <para>
/// 通过 <see cref="IPropertyWriterProvider.SetPropertyAsync"/> 创建新属性。
/// Registry: 创建新 value (若已存在则覆盖, 与 PowerShell 一致)。
/// 支持 <c>-PropertyType</c> 指定 Registry value kind。
/// </para>
/// </summary>
[Verb("New", Noun = "ItemProperty", Aliases = ["np"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Creates a new property on an item.")]
public sealed class NewItemPropertyCommand : ICommand<NewItemPropertyCommand.Args>
{
    /// <summary>Arguments for <c>New-ItemProperty</c>.</summary>
    /// <param name="Path">目标 item 路径。</param>
    /// <param name="Name">属性名。</param>
    /// <param name="Value">属性值。</param>
    /// <param name="PropertyType">Registry value kind: String / ExpandString / DWord / QWord / Binary / MultiString。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string Name,
        [property: Parameter(Position = 2)] object? Value = null,
        [property: Parameter] string? PropertyType = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        if (!ctx.ShouldProcess($"{path.Display} -> {args.Name}={args.Value}", "New-ItemProperty", ConfirmImpact.Medium))
            yield break;

        var writer = ctx.Providers.ResolveCapability<IPropertyWriterProvider>(path);
        if (writer is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support writing properties.",
                TargetPath = path,
                Operation = "new-itemproperty",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        // 若指定了 PropertyType, 将值转换为对应类型 (主要影响 Registry).
        var value = args.Value;
        if (!string.IsNullOrEmpty(args.PropertyType))
        {
            value = ConvertByPropertyType(args.PropertyType, args.Value);
        }

        try
        {
            await writer.SetPropertyAsync(path, args.Name, value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = $"Access denied creating property '{args.Name}' on '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "new-itemproperty",
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
                Message = $"Failed to create property '{args.Name}' on '{path.Display}': {ex.Message}",
                TargetPath = path,
                Operation = "new-itemproperty",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Created property '{args.Name}' on '{path.Display}'.", ct).ConfigureAwait(false);
    }

    /// <summary>按 PropertyType 字符串转换值 (用于 Registry value kind 提示)。</summary>
    private static object? ConvertByPropertyType(string propertyType, object? value)
    {
        var pt = propertyType.Trim();
        return pt.ToLowerInvariant() switch
        {
            "dword" or "int" => value is int ? value : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
            "qword" or "long" => value is long ? value : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
            "binary" or "byte[]" => value is byte[] ? value : ConvertToBytes(value),
            "multistring" or "string[]" => value is string[] ? value : new[] { value?.ToString() ?? "" },
            "expandstring" => value?.ToString() ?? "",
            "string" or _ => value?.ToString() ?? "",
        };
    }

    private static byte[] ConvertToBytes(object? value)
    {
        return value switch
        {
            null => Array.Empty<byte>(),
            byte[] b => b,
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            int i => BitConverter.GetBytes(i),
            long l => BitConverter.GetBytes(l),
            _ => System.Text.Encoding.UTF8.GetBytes(value.ToString() ?? ""),
        };
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
