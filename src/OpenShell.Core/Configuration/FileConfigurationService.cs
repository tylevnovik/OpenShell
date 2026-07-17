using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Configuration;

/// <summary>
/// 基于 TOML 文件的 <see cref="IConfigurationService"/> 默认实现。Per ADR-0022.
/// 持久化到 <c>~/.openshell/config.toml</c>。
/// 文件不存在或解析失败时降级到默认值, 不阻断启动 (ADR-0022 §11 约束)。
/// </summary>
public sealed class FileConfigurationService : IConfigurationService
{
    private readonly string _path;
    private readonly object _lock = new();
    private OpenShellConfig _config = new();

    /// <summary>构造 FileConfigurationService。</summary>
    /// <param name="path">config.toml 文件路径, 默认 <see cref="OpenShell.OpenShellPaths.Config"/>。</param>
    public FileConfigurationService(string? path = null)
    {
        _path = path ?? OpenShellPaths.Config;
    }

    /// <inheritdoc />
    public OpenShellConfig Config
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<OpenShellConfig>? ConfigChanged;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct = default)
    {
        OpenShellConfig loaded;

        if (!File.Exists(_path))
        {
            // 文件不存在: 使用默认值, 不报错。
            loaded = new OpenShellConfig();
        }
        else
        {
            try
            {
                var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
                loaded = ParseConfig(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读取失败: 降级到默认值, 记录 warning。
                Console.Error.WriteLine($"[warn] failed to read config '{_path}': {ex.Message}");
                loaded = new OpenShellConfig();
            }
            catch (Tomlyn.TomlException ex)
            {
                // 解析失败: 降级到默认值 (ADR-0022 §11 约束: 不阻断启动)。
                Console.Error.WriteLine($"[warn] failed to parse config '{_path}': {ex.Message}");
                loaded = new OpenShellConfig();
            }
        }

        lock (_lock)
        {
            _config = loaded;
        }

        ConfigChanged?.Invoke(this, loaded);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken ct = default)
    {
        OpenShellConfig snapshot;
        lock (_lock)
        {
            snapshot = _config;
        }

        var toml = SerializeConfig(snapshot);

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(_path, toml, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[warn] failed to write config '{_path}': {ex.Message}");
            throw;
        }

        ConfigChanged?.Invoke(this, snapshot);
    }

    /// <summary>从 TOML 文本解析配置。校验失败的字段降级到默认值。</summary>
    private static OpenShellConfig ParseConfig(string text)
    {
        var root = Toml.ToModel(text);
        var config = new OpenShellConfig();

        config.Theme = TryGetString(root, "theme") ?? config.Theme;
        config.PromptStyle = TryGetString(root, "promptStyle") ?? config.PromptStyle;
        config.HistorySize = TryGetInt(root, "historySize") ?? config.HistorySize;
        config.MaxParallelOperations = TryGetInt(root, "maxParallelOperations") ?? config.MaxParallelOperations;
        config.ProfileStopOnError = TryGetBool(root, "profileStopOnError") ?? config.ProfileStopOnError;
        config.AutoUpdate = TryGetBool(root, "autoUpdate") ?? config.AutoUpdate;
        config.UpdateChannel = TryGetString(root, "updateChannel") ?? config.UpdateChannel;
        config.UpdateCheckFrequency = TryGetString(root, "updateCheckFrequency") ?? config.UpdateCheckFrequency;
        config.IncludePrerelease = TryGetBool(root, "includePrerelease") ?? config.IncludePrerelease;

        // ADR-0036: 安全沙箱与权限模型配置。
        config.SecurityRole = TryGetString(root, "securityRole") ?? config.SecurityRole;
        config.SecurityStrictness = TryGetString(root, "securityStrictness") ?? config.SecurityStrictness;
        if (root.TryGetValue("protectedPaths", out var ppVal) && ppVal is TomlArray ppArr)
        {
            config.ProtectedPaths.Clear();
            foreach (var item in ppArr)
            {
                if (item is string s) config.ProtectedPaths.Add(s);
            }
        }

        if (root.TryGetValue("aliases", out var aliasVal) && aliasVal is TomlTable aliasTable)
        {
            foreach (var kv in aliasTable)
            {
                if (kv.Value is string s)
                {
                    config.Aliases[kv.Key] = s;
                }
            }
        }

        if (root.TryGetValue("variables", out var varVal) && varVal is TomlTable varTable)
        {
            foreach (var kv in varTable)
            {
                if (kv.Value is string s)
                {
                    config.Variables[kv.Key] = s;
                }
            }
        }

        return config;
    }

    /// <summary>把配置序列化为 TOML 文本。</summary>
    private static string SerializeConfig(OpenShellConfig config)
    {
        var root = new TomlTable
        {
            ["theme"] = config.Theme,
            ["promptStyle"] = config.PromptStyle,
            ["historySize"] = config.HistorySize,
            ["maxParallelOperations"] = config.MaxParallelOperations,
            ["profileStopOnError"] = config.ProfileStopOnError,
            ["autoUpdate"] = config.AutoUpdate,
            ["updateChannel"] = config.UpdateChannel,
            ["updateCheckFrequency"] = config.UpdateCheckFrequency,
            ["includePrerelease"] = config.IncludePrerelease,
            ["securityRole"] = config.SecurityRole,
            ["securityStrictness"] = config.SecurityStrictness,
        };

        if (config.ProtectedPaths.Count > 0)
        {
            var ppArr = new TomlArray();
            foreach (var p in config.ProtectedPaths)
            {
                ppArr.Add(p);
            }
            root["protectedPaths"] = ppArr;
        }

        if (config.Aliases.Count > 0)
        {
            var aliasTable = new TomlTable();
            foreach (var kv in config.Aliases)
            {
                aliasTable[kv.Key] = kv.Value;
            }
            root["aliases"] = aliasTable;
        }

        if (config.Variables.Count > 0)
        {
            var varTable = new TomlTable();
            foreach (var kv in config.Variables)
            {
                varTable[kv.Key] = kv.Value;
            }
            root["variables"] = varTable;
        }

        return Toml.FromModel(root);
    }

    private static string? TryGetString(TomlTable table, string key)
        => table.TryGetValue(key, out var v) ? v as string : null;

    private static int? TryGetInt(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var v)) return null;
        // Tomlyn 整数返回 long, 需转换。
        if (v is long l) return (int)l;
        if (v is int i) return i;
        return null;
    }

    private static bool? TryGetBool(TomlTable table, string key)
        => table.TryGetValue(key, out var v) && v is bool b ? b : null;
}
