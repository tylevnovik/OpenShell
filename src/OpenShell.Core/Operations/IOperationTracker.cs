namespace OpenShell.Operations;

/// <summary>
/// Provider 操作跟踪器抽象。Per ADR-0016 §3 第 2 步: 插件卸载前必须等待所有 in-flight 操作完成 (带超时)。
/// 每次操作开始时 <see cref="Increment"/>, 完成 (无论成功/失败/取消) 时 <see cref="Decrement"/>。
/// 卸载流程通过 <see cref="WaitForProviderAsync"/> 等待归零后再调用 ALC.Unload()。
/// 线程安全: 实现必须支持并发 Increment/Decrement/Wait。
/// </summary>
public interface IOperationTracker
{
    /// <summary>
    /// 标记一次针对指定 provider 的操作开始, in-flight 计数 +1。
    /// </summary>
    /// <param name="providerName">Provider 名称 (来自 <see cref="OpenShell.Paths.ItemPath.Provider"/>), 大小写不敏感。</param>
    void Increment(string providerName);

    /// <summary>
    /// 标记一次针对指定 provider 的操作结束, in-flight 计数 -1。
    /// 计数不会降到负数; 若已为 0 则保持 0。归零时唤醒所有 <see cref="WaitForProviderAsync"/> 等待者。
    /// </summary>
    /// <param name="providerName">Provider 名称, 大小写不敏感。</param>
    void Decrement(string providerName);

    /// <summary>
    /// 获取指定 provider 当前 in-flight 操作数。未跟踪的 provider 返回 0。
    /// </summary>
    int GetInFlightCount(string providerName);

    /// <summary>
    /// 等待指定 provider 的所有 in-flight 操作归零。Per ADR-0016 §3 第 2 步。
    /// 若调用时已为 0, 立即返回 true。
    /// 否则阻塞至归零 (被 <see cref="Decrement"/> 唤醒) 或 <paramref name="cancellationToken"/> 取消。
    /// </summary>
    /// <returns>true 表示归零成功; false 表示等待被取消 (调用方应中止卸载)。</returns>
    ValueTask<bool> WaitForProviderAsync(string providerName, CancellationToken cancellationToken = default);
}
