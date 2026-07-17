using System.Collections;
using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Packaging;

/// <summary>
/// <c>plugins.config.toml</c> 模型。Per ADR-0039 §6.
/// 记录已安装 Provider 的启用状态、加载顺序、自动更新开关与 per-provider 配置。
/// </summary>
public sealed class PluginsConfig
{
    private readonly string _configPath;
    private readonly List<ProviderEntry> _providers;
    private readonly object _lock = new();

    /// <summary>使用默认路径 <see cref="OpenShellPaths.PluginsConfigPath"/> 构造。</summary>
    public PluginsConfig() : this(OpenShellPaths.PluginsConfigPath) { }

    /// <summary>使用自定义路径构造 (测试隔离用)。</summary>
    public PluginsConfig(string configPath)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _providers = new List<ProviderEntry>();
    }

    /// <summary>当前已加载的 Provider 条目 (按 <see cref="ProviderEntry.LoadOrder"/> 升序)。</summary>
    public IReadOnlyList<ProviderEntry> Providers
    {
        get
        {
            lock (_lock)
            {
                return _providers.OrderBy(p => p.LoadOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// 异步加载 <c>plugins.config.toml</c>。Per ADR-0039 §6.
    /// 文件不存在视为空配置; 解析失败返回空列表 (不抛异常)。
    /// </summary>
    public Task LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _providers.Clear();
            if (!File.Exists(_configPath)) return Task.CompletedTask;

            try
            {
                var text = File.ReadAllText(_configPath);
                var root = Toml.ToModel(text, _configPath);
                if (root.TryGetValue("provider", out var arr))
                {
                    // Tomlyn 把 [[provider]] 数组表解析为 TomlTableArray, inline 数组解析为 TomlArray。
                    IEnumerable? entries = arr switch
                    {
                        TomlTableArray tta => tta,
                        TomlArray ta => ta,
                        _ => null,
                    };
                    if (entries is not null)
                    {
                        foreach (var item in entries)
                        {
                            if (item is not TomlTable t) continue;
                            var name = TryGetString(t, "name");
                            if (string.IsNullOrEmpty(name)) continue;
                            var config = new Dictionary<string, object?>(StringComparer.Ordinal);
                            if (t.TryGetValue("config", out var c) && c is TomlTable ct2)
                            {
                                foreach (var kv in ct2) config[kv.Key] = kv.Value;
                            }
                            _providers.Add(new ProviderEntry
                            {
                                Name = name!,
                                Enabled = TryGetBool(t, "enabled", defaultValue: true),
                                LoadOrder = TryGetInt(t, "loadOrder", defaultValue: 100),
                                AutoUpdate = TryGetBool(t, "autoUpdate", defaultValue: false),
                                Config = config,
                            });
                        }
                    }
                }
            }
            catch
            {
                _providers.Clear();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 异步写回 <c>plugins.config.toml</c>。Per ADR-0039 §6.
    /// 自动创建父目录。空列表会写出空 <c>[[provider]]</c> 段。
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<ProviderEntry> snapshot;
        lock (_lock) snapshot = _providers.ToList();

        var root = new TomlTable();
        var arr = new TomlTableArray();
        foreach (var p in snapshot)
        {
            var t = new TomlTable { ["name"] = p.Name, ["enabled"] = p.Enabled, ["loadOrder"] = p.LoadOrder, ["autoUpdate"] = p.AutoUpdate };
            if (p.Config.Count > 0)
            {
                var ct2 = new TomlTable();
                foreach (var kv in p.Config) ct2[kv.Key] = kv.Value ?? string.Empty;
                t["config"] = ct2;
            }
            arr.Add(t);
        }
        root["provider"] = arr;

        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_configPath, Toml.FromModel(root));
        return Task.CompletedTask;
    }

    /// <summary>添加或更新一个 Provider 条目 (按 Name 大小写不敏感匹配)。</summary>
    public void Upsert(ProviderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.Name)) throw new ArgumentException("Entry name is required.");
        lock (_lock)
        {
            var idx = _providers.FindIndex(p => string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _providers[idx] = entry;
            else _providers.Add(entry);
        }
    }

    /// <summary>移除指定名称的 Provider 条目。返回是否成功移除。</summary>
    public bool Remove(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        lock (_lock)
        {
            return _providers.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    /// <summary>按名称查找 Provider 条目 (大小写不敏感)。</summary>
    public ProviderEntry? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_lock)
        {
            return _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string? TryGetString(TomlTable t, string key)
        => t.TryGetValue(key, out var v) ? v as string : null;

    private static int TryGetInt(TomlTable t, string key, int defaultValue)
        => t.TryGetValue(key, out var v) && v is long l ? (int)l : defaultValue;

    private static bool TryGetBool(TomlTable t, string key, bool defaultValue)
        => t.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;
}

/// <summary>
/// <c>plugins.config.toml</c> 中单个 Provider 的启用/加载条目。Per ADR-0039 §6.
/// </summary>
public sealed record ProviderEntry
{
    /// <summary>Provider 名 (与 manifest 的 name 字段一致)。</summary>
    public required string Name { get; init; }

    /// <summary>是否启用。false 表示已安装但不加载 (禁用)。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>加载顺序 (数字越小越先加载)。优先于拓扑序。</summary>
    public int LoadOrder { get; init; } = 100;

    /// <summary>是否开启自动更新 (ADR-0037 协同)。</summary>
    public bool AutoUpdate { get; init; }

    /// <summary>per-provider 配置字典 (如 Region/Profile 等, 由 GUI 配置面板填写)。</summary>
    public IReadOnlyDictionary<string, object?> Config { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
