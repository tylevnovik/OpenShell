namespace OpenShell.Favorites;

/// <summary>
/// 收藏夹服务。Per ADR-0028 §6.
/// <para>
/// 维护用户收藏的路径列表 (provider-qualified)。默认持久化到
/// <c>~/.openshell/favorites.toml</c>, 采用 TOML <c>[[favorite]]</c> 数组表格式。
/// </para>
/// </summary>
public interface IFavoritesService
{
    /// <summary>当前已加载的收藏夹列表 (按文件中的顺序)。</summary>
    IReadOnlyList<Favorite> Favorites { get; }

    /// <summary>
    /// 添加一个收藏夹条目。同名 (大小写不敏感) 条目会被替换。
    /// 持久化到文件并触发 <see cref="FavoritesChanged"/> 事件。
    /// </summary>
    /// <param name="favorite">要添加的收藏夹条目。</param>
    void Add(Favorite favorite);

    /// <summary>
    /// 按名称移除收藏夹条目 (大小写不敏感)。
    /// 持久化到文件并触发 <see cref="FavoritesChanged"/> 事件。
    /// </summary>
    /// <param name="name">要移除的收藏夹名称。</param>
    /// <returns>找到并移除返回 true; 否则返回 false。</returns>
    bool Remove(string name);

    /// <summary>
    /// 重新从文件加载收藏夹列表。文件缺失或解析失败时返回空列表 (不抛异常)。
    /// 触发 <see cref="FavoritesChanged"/> 事件。
    /// </summary>
    void Reload();

    /// <summary>
    /// 收藏夹列表变化时触发 (Add / Remove / Reload 均会触发)。
    /// </summary>
    event EventHandler? FavoritesChanged;
}
