using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Plugins;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Import-Module</c> 命令。Per ADR-0016 §6.
/// 加载一个第三方插件：可指定 <c>.dll</c> 路径或 <c>plugin.manifest.json</c> 路径。
/// <list type="bullet">
/// <item>若指定 .dll: 同目录下有 manifest 则使用 manifest, 否则报错（无 manifest 无法定位 EntryType）。</item>
/// <item>若指定 manifest: 解析 manifest 后调用 <see cref="IPluginLoader.Load"/>。</item>
/// </list>
/// 加载失败不影响主程序，仅输出错误。
/// </summary>
[Verb("Import", Noun = "Module", Aliases = ["imp", "import-module"])]
[Description("Loads a third-party plugin from a .dll or plugin.manifest.json path.")]
[Help(
    Synopsis = "Loads a third-party plugin (Provider + commands) at runtime.",
    Examples = new[]
    {
        "import-module ./MyProvider.dll",
        "import-module ~/.openshell/plugins/my-provider/plugin.manifest.json",
        "imp ./MyProvider.dll",
    },
    RelatedLinks = new[] { "get-module", "remove-module" })]
public sealed class ImportModuleCommand : ICommand<ImportModuleCommand.Args>
{
    /// <summary>Arguments for <c>Import-Module</c>.</summary>
    /// <param name="Path">.dll 路径或 plugin.manifest.json 路径（相对路径相对当前工作目录）。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true, Aliases = new[] { "-p" })]
        string Path);

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
                Operation = "import-module",
                Phase = ErrorPhase.Parse,
            });
            yield break;
        }

        var loader = (IPluginLoader?)services.GetService(typeof(IPluginLoader));
        if (loader is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ConfigurationError,
                Message = "IPluginLoader is not registered in the host DI container.",
                Operation = "import-module",
                Phase = ErrorPhase.Parse,
                Suggestion = "Ensure PluginLoader is registered at startup.",
            });
            yield break;
        }

        var inputPath = args.Path?.Trim() ?? "";
        if (inputPath.Length == 0)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Path is required.",
                Operation = "import-module",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var fullPath = System.IO.Path.GetFullPath(inputPath);
        if (!System.IO.File.Exists(fullPath))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Path not found: {fullPath}",
                Operation = "import-module",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        PluginManifest manifest;
        try
        {
            // 根据文件类型决定如何读取 manifest。
            var ext = System.IO.Path.GetExtension(fullPath);
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            {
                manifest = PluginManifestLoader.Read(fullPath);
            }
            else
            {
                // 视为 .dll, 尝试同目录下的 plugin.manifest.json。
                var inferred = PluginManifestLoader.TryFromAssemblyPath(fullPath);
                if (inferred is null)
                {
                    ctx.Errors?.Write(new ErrorRecord
                    {
                        Category = ErrorCategory.InvalidArgument,
                        Message = $"No plugin.manifest.json next to '{fullPath}'. Provide a manifest file path instead.",
                        Operation = "import-module",
                        Phase = ErrorPhase.Parse,
                    });
                    yield break;
                }
                manifest = inferred;
            }
        }
        catch (PluginLoadException ex)
        {
            ctx.Errors?.Write(ErrorRecord.FromException(ex, operation: "import-module", phase: ErrorPhase.Parse));
            yield break;
        }

        LoadedPlugin loaded;
        try
        {
            loaded = loader.Load(manifest);
        }
        catch (PluginLoadException ex)
        {
            ctx.Errors?.Write(ErrorRecord.FromException(ex, operation: "import-module", phase: ErrorPhase.Operation));
            yield break;
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(ErrorRecord.FromException(
                ex, operation: "import-module", phase: ErrorPhase.Operation));
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Loaded module '{loaded.Name}' v{loaded.Version}", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  providers: {loaded.Providers.Count} ({string.Join(", ", loaded.Providers.Select(p => p.Info.Name))})",
            ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  commands:  {loaded.CommandTypes.Count}", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  assembly: {loaded.AssemblyPath}", ct).ConfigureAwait(false);

        yield break;
    }
}
