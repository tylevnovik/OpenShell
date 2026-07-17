using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenShell.Plugins;

/// <summary>
/// 默认 <see cref="PluginManifest"/> 加载器。Per ADR-0016 §4.
/// 扫描 <c>~/.openshell/plugins/*/plugin.manifest.json</c>，将每个 manifest 的相对路径
/// （<see cref="PluginManifest.AssemblyPath"/>）解析为相对 manifest 文件的绝对路径。
/// </summary>
public static class PluginManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>manifest 文件名约定。</summary>
    public const string ManifestFileName = "plugin.manifest.json";

    /// <summary>
    /// 扫描默认插件目录（<see cref="OpenShellPaths.Plugins"/>）下所有子目录，
    /// 返回找到的 <see cref="PluginManifest"/> 列表（assemblyPath 已转为绝对路径）。
    /// 加载失败的 manifest 不会抛异常，仅 log warning 后跳过。
    /// </summary>
    public static IReadOnlyList<PluginManifest> DiscoverAll(string? pluginsDirectory = null, ILogger? logger = null)
    {
        var dir = pluginsDirectory ?? OpenShellPaths.Plugins;
        var result = new List<PluginManifest>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var manifestPath = Path.Combine(sub, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = Read(manifestPath);
                result.Add(manifest);
            }
            catch (Exception ex)
            {
                // 单个 manifest 解析失败不影响其他插件或主程序启动。Per ADR-0016 §5.
                logger?.LogWarning(ex, "Failed to read plugin manifest at '{Path}'.", manifestPath);
            }
        }

        return result;
    }

    /// <summary>
    /// 从指定 manifest 文件读取。将 <see cref="PluginManifest.AssemblyPath"/> 解析为绝对路径
    /// （相对 manifest 文件所在目录）。
    /// </summary>
    public static PluginManifest Read(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new PluginLoadException($"Plugin manifest not found: {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        var dto = JsonSerializer.Deserialize<PluginManifestDto>(json, JsonOptions)
            ?? throw new PluginLoadException($"Plugin manifest is empty or invalid: {manifestPath}");

        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new PluginLoadException($"Plugin manifest '{manifestPath}' is missing 'name'.");
        if (string.IsNullOrWhiteSpace(dto.AssemblyPath))
            throw new PluginLoadException($"Plugin manifest '{manifestPath}' is missing 'assemblyPath'.");
        if (string.IsNullOrWhiteSpace(dto.EntryType))
            throw new PluginLoadException($"Plugin manifest '{manifestPath}' is missing 'entryType'.");
        if (string.IsNullOrWhiteSpace(dto.Version))
            throw new PluginLoadException($"Plugin manifest '{manifestPath}' is missing 'version'.");

        var manifestDir = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        var absAssemblyPath = Path.GetFullPath(dto.AssemblyPath, manifestDir);

        return new PluginManifest
        {
            Name = dto.Name!,
            Version = ParseVersion(dto.Version!),
            AssemblyPath = absAssemblyPath,
            EntryType = dto.EntryType!,
        };
    }

    /// <summary>从 .dll 路径推断 manifest（同目录下的 <c>plugin.manifest.json</c>）。</summary>
    public static PluginManifest? TryFromAssemblyPath(string assemblyPath)
    {
        var dir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;
        try { return Read(manifestPath); }
        catch { return null; }
    }

    private static Version ParseVersion(string version)
    {
        // 容忍 "1.0" / "1.0.0" / "1.0.0.0" 等格式。
        if (Version.TryParse(version, out var v)) return v;
        return new Version(0, 0);
    }

    /// <summary>JSON DTO（与 manifest 文件结构对应）。</summary>
    private sealed class PluginManifestDto
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? AssemblyPath { get; set; }
        public string? EntryType { get; set; }
    }
}
