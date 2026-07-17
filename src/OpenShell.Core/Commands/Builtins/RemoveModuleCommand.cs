using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Modules;
using OpenShell.Plugins;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Remove-Module</c> 命令。Per ADR-0016 §6 + ADR-0056 §3.
/// 卸载指定名称的插件（IPluginLoader）或脚本模块（ModuleRegistry）。
/// 插件：反注册 providers/commands → 调用 Shutdown → 卸载 ALC。
/// 脚本模块：从 ModuleRegistry 缓存中移除（下次 import 将重新加载）。
/// 未找到时仅提示，不报错。可重入：重复卸载安全。
/// </summary>
[Verb("Remove", Noun = "Module", Aliases = ["rmmod", "remove-module"])]
[Description("Unloads a previously loaded plugin or script module by name.")]
[Help(
    Synopsis = "Unloads a plugin (unregisters providers/commands, unloads ALC) or removes a script module from cache.",
    Examples = new[]
    {
        "remove-module MyProvider",
        "rmmod MyProvider",
    },
    RelatedLinks = new[] { "get-module", "import-module" })]
public sealed class RemoveModuleCommand : ICommand<RemoveModuleCommand.Args>
{
    /// <summary>Arguments for <c>Remove-Module</c>.</summary>
    /// <param name="Name">模块名（插件名或脚本模块名）。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var services = ctx.Host.Services;
        if (services is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ConfigurationError,
                Message = "Host service provider is not available.",
                Operation = "remove-module",
                Phase = ErrorPhase.Parse,
            });
            yield break;
        }

        var name = args.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Module name is required.",
                Operation = "remove-module",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        // ADR-0016 §6: 先尝试插件模块卸载。
        var loader = (IPluginLoader?)services.GetService(typeof(IPluginLoader));
        if (loader is not null && loader.TryGet(name, out _))
        {
            var ok = loader.Unload(name);
            if (ok)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"Unloaded plugin module '{name}'.", ct).ConfigureAwait(false);
            }
            else
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"Failed to unload plugin module '{name}'.", ct).ConfigureAwait(false);
            }
            yield break;
        }

        // ADR-0056 §3: 插件未命中时，尝试脚本模块移除（按 Name 查找，按 FilePath 移除）。
        var moduleRegistry = (ModuleRegistry?)services.GetService(typeof(ModuleRegistry));
        if (moduleRegistry is not null)
        {
            var match = moduleRegistry.Loaded.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                moduleRegistry.Remove(match.FilePath);
                await ctx.Host.WriteOutputLineAsync(
                    $"Removed script module '{name}' ({match.FilePath}).", ct).ConfigureAwait(false);
                yield break;
            }
        }

        await ctx.Host.WriteOutputLineAsync(
            $"No loaded module named '{name}' (nothing to remove).", ct).ConfigureAwait(false);

        yield break;
    }
}
