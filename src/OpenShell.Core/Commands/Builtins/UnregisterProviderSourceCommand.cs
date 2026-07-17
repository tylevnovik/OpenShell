using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Registry;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Unregister-ProviderSource</c> command. Per ADR-0039 §5 / §3.
/// 从 <c>~/.openshell/registries.toml</c> 移除一个注册源。
/// </summary>
[Verb("Unregister", Noun = "ProviderSource", Aliases = ["rmpsrc"])]
[Description("Removes a registered provider registry source.")]
public sealed class UnregisterProviderSourceCommand : ICommand<UnregisterProviderSourceCommand.Args>
{
    /// <summary>Arguments for <c>Unregister-ProviderSource</c>.</summary>
    public record Args
    {
        /// <summary>要移除的源名。必填。</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Name))
        {
            await ctx.Host.WriteOutputLineAsync("usage: unregister-providersource <name>", ct);
            yield break;
        }

        var registry = ctx.Host.Services.GetService(typeof(ProviderSourceRegistry)) as ProviderSourceRegistry;
        if (registry is null)
        {
            await ctx.Host.WriteOutputLineAsync("[unregister-providersource] ProviderSourceRegistry not registered.", ct);
            yield break;
        }

        bool ok;
        try
        {
            ok = registry.RemoveSource(args.Name!);
            if (ok) await registry.SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ctx.Host.WriteOutputLineAsync($"[unregister-providersource] failed: {ex.Message}", ct);
            yield break;
        }

        if (!ok)
        {
            await ctx.Host.WriteOutputLineAsync($"[unregister-providersource] source '{args.Name}' not found.", ct);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync($"unregistered source '{args.Name}'.", ct);
        yield return new Item
        {
            Path = new ItemPath { Provider = "src", InternalPath = "/" + args.Name },
            Kind = ItemKind.Unknown,
            Properties = PropertyBag.Empty
                .With("Name", args.Name!)
                .With("Removed", true),
        };
    }
}
