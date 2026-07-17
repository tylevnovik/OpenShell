namespace OpenShell.Updates;

/// <summary>
/// 自动更新服务接口。Per ADR-0037 §1.
/// 负责检查更新、下载、安装新版本，并暴露状态变更通知。
/// 默认实现 <see cref="GitHubReleasesUpdateService"/> 走 GitHub Releases；
/// 测试/离线环境可用 <see cref="NoopUpdateService"/> 占位。
/// </summary>
public interface IUpdateService
{
    /// <summary>检查是否有可用更新。无更新或检查失败时返回 null。</summary>
    ValueTask<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>下载指定版本到本地 <c>~/.openshell/updates/&lt;version&gt;/</c>，完成后做 SHA256 校验。</summary>
    /// <param name="info">从 <see cref="CheckForUpdatesAsync"/> 获取的更新信息。</param>
    /// <param name="progress">进度回调，参数为 0.0~1.0。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default);

    /// <summary>安装已下载的更新。原子替换当前 exe，旧版本保留为 <c>.old</c>。</summary>
    /// <param name="info">要安装的更新信息 (用于定位下载好的文件)。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask InstallAsync(UpdateInfo info, CancellationToken ct = default);

    /// <summary>
    /// 从本地离线包安装更新。Per ADR-0037 §13 (<c>update-openshell -Offline &lt;path&gt;</c>)。
    /// 不联网检查, 直接打开本地 <c>.exe</c> / <c>.zip</c> 包, 做 SHA256 (若提供) 与代码签名校验后,
    /// 走与 <see cref="InstallAsync"/> 相同的 atomic rename / 独立 updater 流程。
    /// </summary>
    /// <param name="path">本地离线包绝对路径。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask InstallFromOfflineAsync(string path, CancellationToken ct = default);

    /// <summary>状态变更通知流 (Idle/Checking/UpdateAvailable/Downloading/Verifying/ReadyToInstall/Installing/Installed/Failed)。</summary>
    IObservable<UpdateStatus> StatusChanged { get; }
}
