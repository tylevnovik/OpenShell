namespace OpenShell.Packaging.Installation;

/// <summary>
/// Provider 安装生命周期接口。Per ADR-0039 §6 / §7.
/// 实现 install / update / uninstall 三个核心操作, 内部协调注册源查询、下载、签名校验、
/// 解压、current 符号链接更新与 PluginLoader 注册。
/// </summary>
public interface IProviderInstaller
{
    /// <summary>
    /// 安装一个 Provider。Per ADR-0039 §6.
    /// 流程: 解析依赖 → 下载 .osp → 验签 → 解压到 <c>~/.openshell/providers/{name}/{version}/</c>
    ///       → 更新 current 符号链接 → 注册到 PluginLoader。
    /// </summary>
    /// <param name="name">要安装的 Provider 包名。</param>
    /// <param name="version">可选指定版本; 缺省取最新稳定版。</param>
    /// <param name="sourceName">可选指定注册源名; 缺省按优先级遍历所有源。</param>
    /// <param name="dryRun">仅解析依赖与下载清单, 不真正下载 .osp 也不写文件。</param>
    /// <param name="trustKey">用户通过 <c>-TrustKey</c> 显式信任的公钥 (裸字节)。
    /// 当签名校验返回 <see cref="OpenShell.Packaging.Signing.SignatureResult.Untrusted"/>
    /// 且此值与包内嵌公钥逐字节相等时, 视为受信任。Per ADR-0039 §6 / §9.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>安装结果 (含已解析依赖列表、最终版本、安装路径等)。</returns>
    Task<InstallResult> InstallAsync(string name, string? version = null, string? sourceName = null, bool dryRun = false, byte[]? trustKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 升级一个已安装 Provider 到最新稳定版。Per ADR-0039 §6 / §9.
    /// 流程: 查询最新版 → 安装新版本 → 切换 current → (可选)卸载旧版本。
    /// </summary>
    Task<InstallResult> UpdateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 卸载一个已安装 Provider。Per ADR-0039 §6.
    /// 流程: 备份目录到 trash → 删除安装目录 → 移除 current 符号链接 → 反注册 PluginLoader → 更新 plugins.config。
    /// </summary>
    /// <param name="name">Provider 名。</param>
    /// <param name="cancellationToken"></param>
    /// <returns>是否成功卸载 (未安装返回 false)。</returns>
    Task<bool> UninstallAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>查询已安装的 Provider 列表 (扫描 <c>~/.openshell/providers/</c> 下所有子目录)。</summary>
    IReadOnlyList<InstalledProvider> ListInstalled();

    /// <summary>
    /// 重新安装所有在 <c>plugins.config.toml</c> 中记录但磁盘上缺失/损坏的 Provider。Per ADR-0039 §11.
    /// 用于主机升级或目录迁移后恢复 provider 环境。仅处理 <c>Enabled=true</c> 的条目;
    /// 已禁用的 provider 跳过。单个 provider 安装失败不会中断整体恢复流程。
    /// </summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);
}

/// <summary>安装操作结果。</summary>
public sealed record InstallResult
{
    /// <summary>Provider 名。</summary>
    public required string Name { get; init; }

    /// <summary>本次安装/更新后的版本。</summary>
    public required string Version { get; init; }

    /// <summary>安装到磁盘的绝对路径 (<c>~/.openshell/providers/{name}/{version}/</c>)。</summary>
    public string? InstallPath { get; init; }

    /// <summary>当前指向的版本目录 (current 符号链接解析后的实际路径)。</summary>
    public string? CurrentPath { get; init; }

    /// <summary>本次安装来源注册源名 (官方/私有/本地)。</summary>
    public string? Source { get; init; }

    /// <summary>是否为 dry-run (未真正下载安装)。</summary>
    public bool DryRun { get; init; }

    /// <summary>解析出的依赖列表 (含 provider 与 external 两种)。</summary>
    public IReadOnlyList<ResolvedDependency> Dependencies { get; init; } = Array.Empty<ResolvedDependency>();

    /// <summary>本次操作的简短人类可读摘要 (供命令输出)。</summary>
    public string? Summary { get; init; }
}

/// <summary>已安装 Provider 的运行时视图。</summary>
public sealed record InstalledProvider
{
    public required string Name { get; init; }
    /// <summary>所有已安装的版本目录 (不含 current 符号链接)。</summary>
    public required IReadOnlyList<string> Versions { get; init; }
    /// <summary>current 符号链接指向的版本 (无则 null)。</summary>
    public string? CurrentVersion { get; init; }
    /// <summary>安装根目录绝对路径。</summary>
    public required string InstallRoot { get; init; }
}

/// <summary>解析出的依赖 (拓扑排序后)。</summary>
public sealed record ResolvedDependency
{
    /// <summary>依赖名 (provider 名或 NuGet 包名)。</summary>
    public required string Name { get; init; }

    /// <summary>要求版本范围 (NuGet 语法)。</summary>
    public required string RequestedVersion { get; init; }

    /// <summary>解析后选定的具体版本。</summary>
    public string? ResolvedVersion { get; init; }

    /// <summary>"provider" 或 "external"。Per ADR-0039 §2.</summary>
    public string Kind { get; init; } = "provider";

    /// <summary>该依赖是否已被满足 (已安装或可由主机提供)。</summary>
    public bool Satisfied { get; init; }
}
