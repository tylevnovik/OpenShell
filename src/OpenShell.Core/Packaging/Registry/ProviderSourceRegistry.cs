using System.Collections;
using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Packaging.Registry;

/// <summary>
/// 注册源配置仓库。Per ADR-0039 §3.
/// 管理持久化在 <c>~/.openshell/registries.toml</c> 的 <c>[[source]]</c> 列表。
/// 提供 Load / Save / AddSource / RemoveSource / ListSources 接口。
/// 文件不存在时返回空列表 (首次启动); 单条 source 解析失败仅跳过该条, 不阻塞加载。
/// </summary>
public sealed class ProviderSourceRegistry
{
    private readonly string _configPath;
    private readonly List<ProviderSource> _sources;
    private readonly object _lock = new();

    /// <summary>使用默认路径 <see cref="OpenShellPaths.RegistriesConfigPath"/> 构造。</summary>
    public ProviderSourceRegistry() : this(OpenShellPaths.RegistriesConfigPath) { }

    /// <summary>使用自定义配置文件路径构造 (测试隔离用)。</summary>
    public ProviderSourceRegistry(string configPath)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _sources = new List<ProviderSource>();
    }

    /// <summary>配置文件绝对路径 (<c>~/.openshell/registries.toml</c> 或测试注入的自定义路径)。</summary>
    public string ConfigPath => _configPath;

    /// <summary>当前内存中已加载的注册源列表 (按 <see cref="ProviderSource.Priority"/> 升序排列)。</summary>
    public IReadOnlyList<ProviderSource> Sources
    {
        get
        {
            lock (_lock)
            {
                return _sources.OrderBy(s => s.Priority).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// 从 <see cref="ConfigPath"/> 异步加载所有 <c>[[source]]</c> 条目。Per ADR-0039 §3.
    /// 文件不存在时返回空列表; 单条 source 非法 (缺 name/url) 时跳过且不抛异常。
    /// </summary>
    public Task LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _sources.Clear();
            if (!File.Exists(_configPath)) return Task.CompletedTask;

            try
            {
                var text = File.ReadAllText(_configPath);
                var root = Toml.ToModel(text, _configPath);
                if (root.TryGetValue("source", out var arr))
                {
                    // Tomlyn 把 [[source]] 数组表解析为 TomlTableArray, 把 inline 数组解析为 TomlArray。
                    // 两种都接受, 保证向后兼容。
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
                            var url = TryGetString(t, "url");
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                            _sources.Add(new ProviderSource
                            {
                                Name = name!,
                                Url = url!,
                                Priority = TryGetInt(t, "priority", defaultValue: 100),
                                Trusted = TryGetBool(t, "trusted", defaultValue: false),
                                Auth = TryGetString(t, "auth"),
                            });
                        }
                    }
                }
            }
            catch
            {
                // 加载失败视为空列表, 不阻塞主程序启动 (与 ADR-0022 配置加载容错一致)。
                _sources.Clear();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把当前 <see cref="Sources"/> 异步写回 <see cref="ConfigPath"/>。Per ADR-0039 §3.
    /// 自动创建父目录。空列表会写出空文件 (清空配置)。
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<ProviderSource> snapshot;
        lock (_lock) snapshot = _sources.ToList();

        var root = new TomlTable();
        var arr = new TomlTableArray();
        foreach (var s in snapshot)
        {
            var t = new TomlTable { ["name"] = s.Name, ["url"] = s.Url };
            if (s.Priority != 100) t["priority"] = s.Priority;
            if (s.Trusted) t["trusted"] = s.Trusted;
            if (!string.IsNullOrEmpty(s.Auth)) t["auth"] = s.Auth;
            arr.Add(t);
        }
        root["source"] = arr;

        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_configPath, Toml.FromModel(root));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 添加一个新注册源。Per ADR-0039 §3.
    /// 同名源已存在时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void AddSource(ProviderSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(source.Name) || string.IsNullOrEmpty(source.Url))
            throw new ArgumentException("Source name and url are required.");
        lock (_lock)
        {
            if (_sources.Any(s => string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Source '{source.Name}' is already registered.");
            _sources.Add(source);
        }
    }

    /// <summary>
    /// 移除指定名称的注册源。Per ADR-0039 §3. 返回是否成功移除。</summary>
    public bool RemoveSource(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        lock (_lock)
        {
            return _sources.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    /// <summary>按名称查找注册源 (大小写不敏感)。</summary>
    public ProviderSource? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_lock)
        {
            return _sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string? TryGetString(TomlTable t, string key)
        => t.TryGetValue(key, out var v) ? v as string : null;

    private static int TryGetInt(TomlTable t, string key, int defaultValue)
        => t.TryGetValue(key, out var v) && v is long l ? (int)l : defaultValue;

    private static bool TryGetBool(TomlTable t, string key, bool defaultValue)
        => t.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;
}
