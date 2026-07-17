namespace OpenShell.Configuration;

/// <summary>
/// 配置服务。Per ADR-0022.
/// 负责加载/保存 <c>~/.openshell/config.toml</c>, 暴露强类型 <see cref="OpenShellConfig"/>。
/// 支持热重载通知 (ConfigChanged 事件)。
/// </summary>
public interface IConfigurationService
{
    /// <summary>当前已加载的配置 (内存快照)。</summary>
    OpenShellConfig Config { get; }

    /// <summary>从持久化文件加载配置。文件不存在时使用默认值。</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>保存当前配置到持久化文件, 并触发 <see cref="ConfigChanged"/> 事件。</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>配置变更事件 (Load/Save 后触发)。</summary>
    event EventHandler<OpenShellConfig>? ConfigChanged;
}

/// <summary>
/// OpenShell 主配置。Per ADR-0022 §2 (M5 简化版, 扁平结构)。
/// 后续 milestone 可扩展为 ADR-0022 §2 的分组结构 (Shell/Theme/Performance/Operations/Undo/Ipc/Plugins/Remote)。
/// </summary>
public sealed class OpenShellConfig
{
    /// <summary>主题: "dark" / "light" / "system"。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Prompt 样式: "default" / "minimal" / "full"。</summary>
    public string PromptStyle { get; set; } = "default";

    /// <summary>命令历史保留条数。</summary>
    public int HistorySize { get; set; } = 1000;

    /// <summary>最大并行操作数。</summary>
    public int MaxParallelOperations { get; set; } = 4;

    /// <summary>profile 执行遇致命错误是否中断 (ADR-0041 §4)。</summary>
    public bool ProfileStopOnError { get; set; } = true;

    /// <summary>是否启用自动更新 (ADR-0037)。</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>更新通道: "stable" / "beta" / "dev"。Per ADR-0037 §2.</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>自动检查频率: "never" / "startup" / "daily" / "weekly"。Per ADR-0037 §2.</summary>
    public string UpdateCheckFrequency { get; set; } = "daily";

    /// <summary>是否包含预发布版本。Per ADR-0037 §2 / §14. 默认 false.</summary>
    public bool IncludePrerelease { get; set; } = false;

    // ADR-0036: 安全沙箱与权限模型配置。Per ADR-0036 §9 / §14 / §4.

    /// <summary>用户角色: "user" / "admin" / "restricted"。Per ADR-0036 §9. 默认 "user"。</summary>
    public string SecurityRole { get; set; } = "user";

    /// <summary>严格度: "lax" / "default" / "strict" / "paranoid"。Per ADR-0036 §14. 默认 "default"。</summary>
    public string SecurityStrictness { get; set; } = "default";

    /// <summary>
    /// 用户自定义受保护路径列表 (与内置默认值合并)。Per ADR-0036 §4. 默认空列表 (使用 ProtectedPathRegistry 内置默认值)。
    /// </summary>
    public List<string> ProtectedPaths { get; set; } = new();

    /// <summary>
    /// 是否允许在 GUI 宿主中生成子进程。Per ADR-0036 §12. 默认 false。
    /// CLI 宿主不受此限制; GUI 宿主默认禁止 <c>Start-Process</c> 以防用户误触恶意脚本弹窗。
    /// 设为 true 后 GUI 宿主中的 <c>Start-Process</c> 通过 <see cref="OpenShell.Security.ProcessSpawnGuard"/> 检查。
    /// </summary>
    public bool AllowProcessSpawnInGui { get; set; } = false;

    // ADR-0054: 脚本执行策略配置。Per ADR-0054 §10.

    /// <summary>
    /// 脚本执行策略 (User scope): "Restricted" / "RemoteSigned" / "Unrestricted" / "Bypass"。
    /// Per ADR-0054 §10. 默认 "RemoteSigned"。有效策略取 Process > User > Machine 最高优先级。
    /// </summary>
    public string ExecutionPolicy { get; set; } = "RemoteSigned";

    /// <summary>用户自定义别名 (name → command)。</summary>
    public Dictionary<string, string> Aliases { get; set; } = new();

    /// <summary>用户自定义变量 (name → value)。</summary>
    public Dictionary<string, string> Variables { get; set; } = new();

    // T-447: 窗口尺寸/位置持久化。Per ADR-0013 §6.
    // null 表示未持久化（首次启动使用默认值）。

    /// <summary>主窗口 X 坐标（屏幕坐标）。null 表示使用默认居中。</summary>
    public double? WindowX { get; set; }

    /// <summary>主窗口 Y 坐标（屏幕坐标）。null 表示使用默认居中。</summary>
    public double? WindowY { get; set; }

    /// <summary>主窗口宽度。null 表示使用默认 1200。</summary>
    public double? WindowWidth { get; set; }

    /// <summary>主窗口高度。null 表示使用默认 800。</summary>
    public double? WindowHeight { get; set; }

    /// <summary>主窗口是否最大化。null/false 表示未最大化。</summary>
    public bool? WindowMaximized { get; set; }

    // D-27: 列排序状态持久化。null 表示使用默认值（Name 升序）。
    /// <summary>排序序列名: "Name"/"Size"/"Type"/"Modified"。null 表示默认 Name。</summary>
    public string? SortColumn { get; set; }

    /// <summary>排序方向: "Ascending"/"Descending"。null 表示默认 Ascending。</summary>
    public string? SortDirection { get; set; }

    // ADR-0016 §8: 插件热重载配置。

    /// <summary>
    /// 是否监视插件目录变化。Per ADR-0016 §8. 默认 false。
    /// 启用后 FileSystemWatcher 监视 ~/.openshell/plugins/ 下文件变化, 自动重载受影响插件。
    /// </summary>
    public bool PluginWatch { get; set; } = false;

    /// <summary>
    /// 是否启用插件热重载。Per ADR-0016 §8. 默认 false。
    /// 启用后, 检测到插件 DLL/manifest 变化时触发 UnloadAsync → 等待 1 秒 → Load 流程。
    /// 隐含 <see cref="PluginWatch"/> = true。
    /// </summary>
    public bool PluginHotReload { get; set; } = false;

    // ADR-0034 §9: 跨机器会话同步配置。Per ADR-0034 §9 / §13. 默认关闭。

    /// <summary>
    /// 会话同步后端: "none" / "webdav"。Per ADR-0034 §9.
    /// "none" 表示禁用同步; "webdav" 覆盖 Nextcloud / ownCloud 等 WebDAV 兼容服务。
    /// S3 暂不支持 (v1 范围外)。
    /// </summary>
    public string SyncProvider { get; set; } = "none";

    /// <summary>
    /// 同步端点 URL。Per ADR-0034 §9.
    /// WebDAV 示例: <c>https://nc.example.com/dav/openshell-sessions/</c>。
    /// 同步路径为 <c>&lt;SyncEndpoint&gt;/sessions/&lt;id&gt;.json</c>。
    /// </summary>
    public string? SyncEndpoint { get; set; }

    /// <summary>
    /// 同步凭据引用名。Per ADR-0034 §9 / ADR-0019 §3.
    /// 指向 ICredentialProvider 中存储的凭据条目名 (host+user), 用于 WebDAV Basic Auth。
    /// 未配置时使用匿名访问 (部分 WebDAV 服务支持)。
    /// </summary>
    public string? SyncCredentialRef { get; set; }
}
