using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Installation;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Update-Provider</c> command. Per ADR-0039 §5 / §9.
/// 升级一个已安装 Provider 到最新稳定版 (安装新版 + 切换 current)。
/// </summary>
[Verb("Update", Noun = "Provider", Aliases = ["upr"])]
[Description("Updates an installed provider to the latest stable version.")]
public sealed class UpdateProviderCommand : ICommand<UpdateProviderCommand.Args>
{
    /// <summary>Arguments for <c>Update-Provider</c>.</summary>
    public record Args
    {
        /// <summary>要升级的 Provider 名。必填。</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Name))
        {
            await ctx.Host.WriteOutputLineAsync("usage: update-provider <name>", ct);
            yield break;
        }

        var installer = ctx.Host.Services.GetService(typeof(IProviderInstaller)) as IProviderInstaller;
        if (installer is null)
        {
            await ctx.Host.WriteOutputLineAsync("[update-provider] IProviderInstaller not registered.", ct);
            yield break;
        }

        InstallResult result;
        try
        {
            result = await installer.UpdateAsync(args.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ctx.Host.WriteOutputLineAsync($"[update-provider] failed: {ex.Message}", ct);
            yield break;
        }

        if (!string.IsNullOrEmpty(result.Summary))
            await ctx.Host.WriteOutputLineAsync(result.Summary, ct);

        yield return new Item
        {
            Path = new ItemPath { Provider = "update", InternalPath = "/" + result.Name },
            Kind = ItemKind.Unknown,
            Properties = PropertyBag.Empty
                .With("Name", result.Name)
                .With("Version", result.Version)
                .With("Source", result.Source ?? string.Empty)
                .With("InstallPath", result.InstallPath ?? string.Empty)
                .With("CurrentPath", result.CurrentPath ?? string.Empty),
        };
    }
}
