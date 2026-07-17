using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-ItemProperty</c> 命令。读取指定 item 的属性。Per ADR-0018 §5 + ADR-0023 M4.
/// <para>
/// 通过 <see cref="IPropertyProvider"/> 读取属性, 返回包含属性集的 IItem。
/// Registry provider 返回所有 values; FileSystem 返回文件元信息。
/// </para>
/// </summary>
[Verb("Get", Noun = "ItemProperty", Aliases = ["gp"])]
[Description("Gets the properties of an item.")]
public sealed class GetItemPropertyCommand : ICommand<GetItemPropertyCommand.Args>, OpenShell.Pipeline.IPipelineSource
{
    /// <summary>Arguments for <c>Get-ItemProperty</c>.</summary>
    /// <param name="Path">目标 item 路径。</param>
    /// <param name="Name">可选: 仅返回指定名称的属性 (支持多个)。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter] string[]? Name = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        var propProvider = ctx.Providers.ResolveCapability<IPropertyProvider>(path);
        if (propProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support properties.",
                TargetPath = path,
                Operation = "get-itemproperty",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        // 先取 item (用于 PropertyBag 挂载), 再取属性.
        var itemProvider = ctx.Providers.ResolveCapability<IItemProvider>(path);
        IItem? item = null;
        if (itemProvider is not null)
        {
            item = await itemProvider.GetItemAsync(path, ct).ConfigureAwait(false);
        }
        item ??= new Item { Path = path, Kind = ItemKind.Property };

        var props = await propProvider.GetPropertiesAsync(item, ct).ConfigureAwait(false);

        // 过滤: 若指定了 -Name, 仅保留匹配的属性.
        if (args.Name is { Length: > 0 } names)
        {
            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var filtered = PropertyBag.Empty;
            foreach (var (key, value) in props.Values)
            {
                if (nameSet.Contains(key))
                    filtered = filtered.With(key, value);
            }
            props = filtered;
        }

        yield return new Item
        {
            Path = path,
            Kind = ItemKind.Property,
            Properties = props,
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
