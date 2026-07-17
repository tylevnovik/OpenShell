using System.Collections;
using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Favorites;

/// <summary>
/// 基于 TOML 文件的 <see cref="IFavoritesService"/> 默认实现。Per ADR-0028 §6.
/// <para>
/// 持久化到 <c>~/.openshell/favorites.toml</c>, 采用 <c>[[favorite]]</c> 数组表格式。
/// 文件缺失或解析失败时静默返回空列表, 不阻断启动。同名条目按大小写不敏感替换。
/// </para>
/// </summary>
public sealed class FileFavoritesService : IFavoritesService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<Favorite> _favorites;

    /// <summary>
    /// 构造 FileFavoritesService。
    /// </summary>
    /// <param name="filePath">favorites.toml 文件路径; 默认 <see cref="OpenShellPaths.FavoritesFile"/>; 测试可注入。</param>
    public FileFavoritesService(string? filePath = null)
    {
        _filePath = filePath ?? OpenShellPaths.FavoritesFile;
        _favorites = Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<Favorite> Favorites
    {
        get
        {
            lock (_lock)
            {
                return _favorites.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Add(Favorite favorite)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        if (string.IsNullOrEmpty(favorite.Name))
            throw new ArgumentException("Favorite name is required.", nameof(favorite));
        if (string.IsNullOrEmpty(favorite.Path))
            throw new ArgumentException("Favorite path is required.", nameof(favorite));

        lock (_lock)
        {
            // 同名 (大小写不敏感) 替换; 否则追加。
            var idx = _favorites.FindIndex(
                f => string.Equals(f.Name, favorite.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _favorites[idx] = favorite;
            }
            else
            {
                _favorites.Add(favorite);
            }

            Persist();
        }

        OnFavoritesChanged();
    }

    /// <inheritdoc />
    public bool Remove(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        bool removed;
        lock (_lock)
        {
            removed = _favorites.RemoveAll(
                f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Persist();
            }
        }

        if (removed)
        {
            OnFavoritesChanged();
        }
        return removed;
    }

    /// <inheritdoc />
    public void Reload()
    {
        lock (_lock)
        {
            _favorites = Load();
        }
        OnFavoritesChanged();
    }

    /// <inheritdoc />
    public event EventHandler? FavoritesChanged;

    private void OnFavoritesChanged()
    {
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从 <c>favorites.toml</c> 加载收藏夹列表。文件缺失或解析失败返回空列表 (不抛异常)。
    /// </summary>
    private List<Favorite> Load()
    {
        var result = new List<Favorite>();
        if (!File.Exists(_filePath)) return result;

        try
        {
            var text = File.ReadAllText(_filePath);
            var root = Toml.ToModel(text, _filePath);
            if (!root.TryGetValue("favorite", out var arr)) return result;

            // Tomlyn 把 [[favorite]] 数组表解析为 TomlTableArray; 同时兼容 inline TomlArray。
            IEnumerable? entries = arr switch
            {
                TomlTableArray tta => tta,
                TomlArray ta => ta,
                _ => null,
            };
            if (entries is null) return result;

            foreach (var item in entries)
            {
                if (item is not TomlTable table) continue;
                var name = TryGetString(table, "name");
                var path = TryGetString(table, "path");
                if (name is null || path is null) continue;
                result.Add(new Favorite(name, path));
            }
        }
        catch
        {
            // 解析失败: 静默降级为空列表 (graceful degradation)。
            result.Clear();
        }

        return result;
    }

    /// <summary>
    /// 把内存中的收藏夹列表整体重写到 favorites.toml。自动创建父目录。
    /// 调用方必须已持有 <see cref="_lock"/>。
    /// </summary>
    private void Persist()
    {
        var root = new TomlTable();
        // 使用 TomlTableArray 保证 [[favorite]] 数组表语义 (参考 PluginsConfig.SaveAsync)。
        var arr = new TomlTableArray();
        foreach (var f in _favorites)
        {
            var entry = new TomlTable
            {
                ["name"] = f.Name,
                ["path"] = f.Path,
            };
            arr.Add(entry);
        }
        root["favorite"] = arr;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, Toml.FromModel(root));
    }

    private static string? TryGetString(TomlTable table, string key)
        => table.TryGetValue(key, out var v) ? v as string : null;
}
