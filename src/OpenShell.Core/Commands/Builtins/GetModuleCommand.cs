using System.Runtime.CompilerServices;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Modules;
using OpenShell.Plugins;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-Module</c> 命令。Per ADR-0016 §6 + ADR-0056 §3.
/// 列出当前已加载的所有插件（IPluginLoader）与脚本模块（ModuleRegistry）。
/// </summary>
[Verb("Get", Noun = "Module", Aliases = ["gmod", "modules"])]
[Description("Lists all loaded plugins and script modules.")]
[Help(
    Synopsis = "Lists all currently loaded plugins and script modules.",
    Examples = new[]
    {
        "get-module",
        "gmod",
        "modules",
    },
    RelatedLinks = new[] { "import-module", "remove-module" })]
public sealed class GetModuleCommand : ICommand<GetModuleCommand.Args>
{
    private const int NameWidth = 24;
    private const int VersionWidth = 12;
    private const int CountWidth = 10;
    private const int TimeWidth = 22;

    /// <summary>Arguments for <c>Get-Module</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var services = ctx.Host.Services;
        if (services is null)
        {
            await ctx.Host.WriteOutputLineAsync("get-module: host service provider is not available.", ct)
                .ConfigureAwait(false);
            yield break;
        }

        // ADR-0016 §6: 插件模块（ALC 加载的 .dll 插件）。
        var loader = (IPluginLoader?)services.GetService(typeof(IPluginLoader));
        var hasPlugins = false;
        if (loader is not null && loader.Loaded.Count > 0)
        {
            hasPlugins = true;
            await ctx.Host.WriteOutputLineAsync(
                "Name".PadRight(NameWidth)
                + "Version".PadRight(VersionWidth)
                + "Providers".PadRight(CountWidth)
                + "Commands".PadRight(CountWidth)
                + "LoadedAt", ct).ConfigureAwait(false);

            foreach (var p in loader.Loaded.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                await ctx.Host.WriteOutputLineAsync(
                    p.Name.PadRight(NameWidth)
                    + p.Version.ToString().PadRight(VersionWidth)
                    + p.Providers.Count.ToString().PadRight(CountWidth)
                    + p.CommandTypes.Count.ToString().PadRight(CountWidth)
                    + p.LoadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), ct).ConfigureAwait(false);
            }
        }

        // ADR-0056 §3: 脚本模块（.osh export/import 加载的模块）。
        var moduleRegistry = (ModuleRegistry?)services.GetService(typeof(ModuleRegistry));
        var hasScriptModules = false;
        if (moduleRegistry is not null && moduleRegistry.Loaded.Count > 0)
        {
            hasScriptModules = true;
            if (hasPlugins)
                await ctx.Host.WriteOutputLineAsync("", ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync("--- Script Modules ---", ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync(
                "Name".PadRight(NameWidth)
                + "Exports".PadRight(CountWidth)
                + "Path".PadRight(40)
                + "LoadedAt", ct).ConfigureAwait(false);

            foreach (var m in moduleRegistry.Loaded.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var exportCount = m.ExportedFunctions.Count + m.ExportedConstants.Count
                    + (m.DefaultExport is not null ? 1 : 0);
                await ctx.Host.WriteOutputLineAsync(
                    m.Name.PadRight(NameWidth)
                    + exportCount.ToString().PadRight(CountWidth)
                    + m.FilePath.PadRight(40)
                    + m.LoadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), ct).ConfigureAwait(false);
            }
        }

        if (!hasPlugins && !hasScriptModules)
        {
            await ctx.Host.WriteOutputLineAsync("(no modules loaded)", ct).ConfigureAwait(false);
        }

        yield break;
    }
}
