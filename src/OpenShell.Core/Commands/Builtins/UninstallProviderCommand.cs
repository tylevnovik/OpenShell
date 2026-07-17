using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Installation;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Uninstall-Provider</c> command. Per ADR-0039 §5 / §6.
/// 卸载一个已安装 Provider (备份到 trash → 删除目录 → 反注册 → 更新 plugins.config)。
/// </summary>
[Verb("Uninstall", Noun = "Provider", Aliases = ["rmpr"])]
[Description("Uninstalls a previously installed provider package.")]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]
public sealed class UninstallProviderCommand : ICommand<UninstallProviderCommand.Args>
{
    /// <summary>Arguments for <c>Uninstall-Provider</c>.</summary>
    public record Args
    {
        /// <summary>要卸载的 Provider 名。必填。</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Name))
        {
            await ctx.Host.WriteOutputLineAsync("usage: uninstall-provider <name>", ct);
            yield break;
        }

        var installer = ctx.Host.Services.GetService(typeof(IProviderInstaller)) as IProviderInstaller;
        if (installer is null)
        {
            await ctx.Host.WriteOutputLineAsync("[uninstall-provider] IProviderInstaller not registered.", ct);
            yield break;
        }

        // ADR-0049 §7: gate the destructive uninstall.
        if (!ctx.ShouldProcess($"provider '{args.Name}'", "Uninstall", ConfirmImpact.High)) yield break;

        bool ok;
        try
        {
            ok = await installer.UninstallAsync(args.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ctx.Host.WriteOutputLineAsync($"[uninstall-provider] failed: {ex.Message}", ct);
            yield break;
        }

        if (!ok)
        {
            await ctx.Host.WriteOutputLineAsync($"[uninstall-provider] '{args.Name}' is not installed.", ct);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync($"uninstalled '{args.Name}'.", ct);
        yield return new Item
        {
            Path = new ItemPath { Provider = "uninstall", InternalPath = "/" + args.Name },
            Kind = ItemKind.Unknown,
            Properties = PropertyBag.Empty
                .With("Name", args.Name)
                .With("Uninstalled", true),
        };
    }
}
