namespace OpenShell;

/// <summary>
/// 统一的持久化路径助手。Per ADR-0022 §1.
/// 所有持久化数据位于 <c>~/.openshell/</c> 下，跨平台通过 <c>Environment.SpecialFolder.UserProfile</c> 解析。
/// </summary>
public static class OpenShellPaths
{
    /// <summary>根目录 <c>~/.openshell</c>。</summary>
    public static string Root { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        + Path.DirectorySeparatorChar
        + ".openshell";

    /// <summary>主配置文件 <c>config.toml</c>。</summary>
    public static string Config => Path.Combine(Root, "config.toml");

    /// <summary>命令历史 <c>history.jsonl</c> (JSON Lines, append-only)。</summary>
    public static string History => Path.Combine(Root, "history.jsonl");

    /// <summary>操作日志 <c>journal.jsonl</c> (Undo/Redo 用, JSON Lines)。</summary>
    public static string Journal => Path.Combine(Root, "journal.jsonl");

    /// <summary>自定义视图目录 <c>views/</c>。</summary>
    public static string Views => Path.Combine(Root, "views");

    /// <summary>Trash 临时备份目录 <c>trash/</c>。</summary>
    public static string Trash => Path.Combine(Root, "trash");

    /// <summary>缓存目录 <c>cache/</c> (可清理)。</summary>
    public static string Cache => Path.Combine(Root, "cache");

    /// <summary>加密凭据存储 <c>credentials.enc</c>。</summary>
    public static string Credentials => Path.Combine(Root, "credentials.enc");

    /// <summary>远程账户配置 <c>remotes.toml</c> (不含凭据)。</summary>
    public static string Remotes => Path.Combine(Root, "remotes.toml");

    /// <summary>未完成的 multipart uploads 目录。</summary>
    public static string Uploads => Path.Combine(Root, "uploads");

    /// <summary>国际化资源目录 <c>locales/</c>。Per ADR-0035. 用户 locale JSON 文件存放于此。</summary>
    public static string LocalesDir => Path.Combine(Root, "locales");

    /// <summary>用户 locale 文件 <c>locales/{locale}.json</c> 的完整路径。Per ADR-0035.</summary>
    /// <param name="locale">BCP 47 locale 标签 (如 <c>en-US</c> / <c>zh-CN</c> / <c>ja-JP</c>)。</param>
    public static string LocaleFile(string locale) => Path.Combine(LocalesDir, $"{locale}.json");

    /// <summary>
    /// 确保 ADR-0035 国际化所需目录 <c>locales/</c> 存在。i18n 服务初始化前调用。
    /// 幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsureLocalesDir()
    {
        EnsureRoot();
        Directory.CreateDirectory(LocalesDir);
    }

    /// <summary>
    /// 第三方插件目录 <c>plugins/</c>。Per ADR-0016.
    /// 每个子目录包含一个 <c>plugin.manifest.json</c> + 主程序集 + 私有依赖。
    /// </summary>
    public static string Plugins => Path.Combine(Root, "plugins");

    /// <summary>日志目录 <c>logs/</c>。Per ADR-0031 §3, 文件轮转 + 保留 7 天。</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>错误日志持久化文件 <c>errors.jsonl</c>。Per ADR-0026.</summary>
    public static string ErrorsLog => Path.Combine(Root, "errors.jsonl");

    /// <summary>用户自定义快捷键配置 <c>keybindings.toml</c>。Per ADR-0027 §4.</summary>
    public static string KeyBindingsFile => Path.Combine(Root, "keybindings.toml");

    // ADR-0029: 剪贴板历史路径。Per ADR-0029 §13.

    /// <summary>剪贴板历史 <c>clipboard-history.jsonl</c> (JSON Lines, append-only, 0600)。Per ADR-0029 §13.</summary>
    public static string ClipboardHistory => Path.Combine(Root, "clipboard-history.jsonl");

    // ADR-0039: Provider 包生态路径。Per ADR-0039 §6.

    /// <summary>Provider 包安装根目录 <c>providers/</c>。每个 Provider 一个子目录, 内含版本子目录 + current 符号链接。Per ADR-0039 §6.</summary>
    public static string ProvidersDir => Path.Combine(Root, "providers");

    /// <summary>Provider 包下载缓存目录 <c>cache/downloads/</c>。按包哈希缓存 .osp 文件。Per ADR-0039 §6.</summary>
    public static string ProviderCacheDir => Path.Combine(Cache, "downloads");

    /// <summary>注册源索引快照目录 <c>cache/indices/</c>。Per ADR-0039 §6 / §11.</summary>
    public static string RegistryIndicesDir => Path.Combine(Cache, "indices");

    /// <summary>注册源配置文件 <c>registries.toml</c>。Per ADR-0039 §3.</summary>
    public static string RegistriesConfigPath => Path.Combine(Root, "registries.toml");

    /// <summary>插件启用/禁用与加载顺序配置 <c>plugins.config.toml</c>。Per ADR-0039 §6.</summary>
    public static string PluginsConfigPath => Path.Combine(Root, "plugins.config.toml");

    // ADR-0037: 自动更新机制路径。

    /// <summary>自动更新工作目录 <c>updates/</c>。下载临时文件、状态文件均存放于此。Per ADR-0037 §4.</summary>
    public static string UpdatesDir => Path.Combine(Root, "updates");

    /// <summary>更新状态文件 <c>updates/state.json</c>。持久化最后检查时间，用于 24h 内不重复检查。Per ADR-0037 §2.</summary>
    public static string UpdateStateFile => Path.Combine(UpdatesDir, "state.json");

    // ADR-0034: 会话与状态恢复路径。

    /// <summary>会话状态目录 <c>sessions/</c>。每会话一个 JSON 文件 + .lock 锁文件。Per ADR-0034 §2.</summary>
    public static string SessionsDir => Path.Combine(Root, "sessions");

    /// <summary>快照目录 <c>snapshots/</c>。用户主动保存的工作区快照。Per ADR-0034 §8.</summary>
    public static string SnapshotsDir => Path.Combine(Root, "snapshots");

    // ADR-0030: 预览与搜索路径。

    /// <summary>预览缓存目录 <c>cache/previews/</c>。Per ADR-0030 §3, LRU 1000 张缩略图。</summary>
    public static string PreviewsCacheDir => Path.Combine(Cache, "previews");

    /// <summary>文件名索引文件 <c>cache/filename-index.db</c>。Per ADR-0030 §4: Everything 风格 USN/walk 索引 (二进制格式, 非 SQLite)。</summary>
    public static string FileNameIndexFile => Path.Combine(Cache, "filename-index.db");

    /// <summary>长期文件索引目录 <c>index/</c>。Per ADR-0030 §8: SQLite + FTS5 全文索引。</summary>
    public static string IndexDir => Path.Combine(Root, "index");

    /// <summary>长期文件索引 SQLite 文件 <c>index/files.db</c>。Per ADR-0030 §8。</summary>
    public static string FileIndexDb => Path.Combine(IndexDir, "files.db");

    /// <summary>
    /// 确保 ADR-0030 预览/搜索所需目录存在。预览缓存 + 索引目录。
    /// 幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsurePreviewDirs()
    {
        EnsureRoot();
        Directory.CreateDirectory(PreviewsCacheDir);
        Directory.CreateDirectory(IndexDir);
    }

    // ADR-0036: 安全沙箱与权限模型路径。

    /// <summary>操作审计日志 <c>audit.jsonl</c> (JSON Lines, append-only, 0600)。Per ADR-0036 §5.</summary>
    public static string AuditLog => Path.Combine(Root, "audit.jsonl");

    // ADR-0028: 收藏夹与最近访问路径。Per ADR-0028 §6-7.

    /// <summary>收藏夹配置 <c>favorites.toml</c> (TOML array-of-tables)。Per ADR-0028 §6.</summary>
    public static string FavoritesFile => Path.Combine(Root, "favorites.toml");

    /// <summary>最近访问列表 <c>recent.jsonl</c> (JSON Lines, 整体重写)。Per ADR-0028 §7.</summary>
    public static string RecentFile => Path.Combine(Root, "recent.jsonl");

    /// <summary>
    /// 确保 ADR-0039 Provider 包生态所需目录存在。注册源/安装器初始化前调用。
    /// 幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsurePackagingDirs()
    {
        EnsureRoot();
        Directory.CreateDirectory(ProvidersDir);
        Directory.CreateDirectory(ProviderCacheDir);
        Directory.CreateDirectory(RegistryIndicesDir);
    }

    /// <summary>
    /// 确保 ADR-0037 自动更新所需目录存在。CheckForUpdatesAsync / DownloadAsync 调用前必调。
    /// 幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsureUpdatesDirs()
    {
        EnsureRoot();
        Directory.CreateDirectory(UpdatesDir);
    }

    /// <summary>
    /// 确保 ADR-0034 会话与状态恢复所需目录存在。会话加载 / 快照保存前必调。
    /// 幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsureSessionDirs()
    {
        EnsureRoot();
        Directory.CreateDirectory(SessionsDir);
        Directory.CreateDirectory(SnapshotsDir);
    }

    // ADR-0027: Themes subsystem paths.

    /// <summary>主题目录 <c>themes/</c>。Per ADR-0027 section 1. 每个主题一个 toml 文件。</summary>
    public static string ThemesDir => Path.Combine(Root, "themes");

    /// <summary>
    /// 确保 ADR-0027 主题所需目录存在。ThemeService 初始化前调用。
    /// 幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsureThemesDir()
    {
        EnsureRoot();
        Directory.CreateDirectory(ThemesDir);
    }

    /// <summary>
    /// 确保 ADR-0029 剪贴板历史所需目录存在 (历史文件位于 Root 下, 故仅需 EnsureRoot)。
    /// ClipboardHistoryService 初始化前调用。幂等: 已存在的目录不会被重建。
    /// </summary>
    public static void EnsureClipboardDirs()
    {
        EnsureRoot();
    }

    /// <summary>确保根目录存在。配置/历史加载前调用。</summary>
    public static void EnsureRoot()
    {
        if (!Directory.Exists(Root))
        {
            Directory.CreateDirectory(Root);
        }
    }
}
