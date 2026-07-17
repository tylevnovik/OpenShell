namespace OpenShell.Recent;

/// <summary>
/// 最近访问服务。Per ADR-0028 §7.
/// <para>
/// 记录用户最近访问过的路径 (provider-qualified), 默认保留 20 条。
/// 持久化到 <c>~/.openshell/recent.jsonl</c>, JSON Lines 格式, 整体重写。
/// </para>
/// <para>
/// 仅记录交互式访问: 调用方决定何时调用 <see cref="RecordAccess"/>。
/// 服务本身不做"交互 vs 命令"过滤。
/// </para>
/// </summary>
public interface IRecentService
{
    /// <summary>当前已加载的最近访问列表 (最近访问在前)。</summary>
    IReadOnlyList<RecentEntry> Recent { get; }

    /// <summary>
    /// 记录一次路径访问。若路径已存在则更新时间戳并移到顶部; 否则前插。
    /// 超过容量上限时丢弃最旧条目。持久化并触发 <see cref="RecentChanged"/> 事件。
    /// </summary>
    /// <param name="path">被访问的 provider-qualified 路径。</param>
    void RecordAccess(string path);

    /// <summary>
    /// 清空最近访问列表并删除持久化文件。触发 <see cref="RecentChanged"/> 事件。
    /// </summary>
    void Clear();

    /// <summary>
    /// 重新从文件加载最近访问列表。文件缺失或行损坏时静默跳过 (不抛异常)。
    /// 触发 <see cref="RecentChanged"/> 事件。
    /// </summary>
    void Reload();

    /// <summary>
    /// 最近访问列表变化时触发 (RecordAccess / Clear / Reload 均会触发)。
    /// </summary>
    event EventHandler? RecentChanged;
}
