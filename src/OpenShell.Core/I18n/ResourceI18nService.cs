using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenShell.I18n;

/// <summary>
/// 基于 JSON 资源文件的 <see cref="II18nService"/> 默认实现。Per ADR-0035.
/// 内置 en-US / zh-CN / ja-JP 三个 locale 的翻译集。
/// 用户可在 <c>~/.openshell/locales/{locale}.json</c> 中提供自定义翻译,
/// 加载时与内置表合并 (用户条目覆盖内置同名条目)。
/// fallback 链: 当前 locale → en-US (源语言) → key 本身。
/// 启动 locale 为 zh-CN (Per i18n 改造: 默认中文界面)。
/// </summary>
public sealed class ResourceI18nService : II18nService
{
    /// <summary>fallback locale (源语言)。缺失 key 时回退到此 locale。</summary>
    public const string DefaultLocale = "en-US";

    /// <summary>启动 locale (用户首次启动时使用的语言)。</summary>
    public const string StartupLocale = "zh-CN";

    /// <summary>内置 locale, 顺序固定 (AvailableLocales 中内置项排在用户项之前)。</summary>
    private static readonly string[] BuiltInLocales = { "zh-CN", "en-US", "ja-JP" };

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _translations = new();
    private readonly HashSet<string> _loadedLocales = new(StringComparer.Ordinal);
    private readonly object _loadedLock = new();
    private readonly string _localesDir;
    private volatile string _currentLocale = StartupLocale;

    /// <summary>
    /// 构造 ResourceI18nService, 加载内置 locale 表。
    /// </summary>
    /// <param name="localesDir">
    /// 用户 locale JSON 文件所在目录。默认 <c>OpenShellPaths.LocalesDir</c> (<c>~/.openshell/locales</c>)。
    /// 测试可传入临时目录隔离。
    /// </param>
    public ResourceI18nService(string? localesDir = null)
    {
        _localesDir = localesDir ?? OpenShellPaths.LocalesDir;

        _translations[DefaultLocale] = BuiltIn_en_US();
        _translations["zh-CN"] = BuiltIn_zh_CN();
        _translations["ja-JP"] = BuiltIn_ja_JP();
    }

    /// <inheritdoc />
    public string CurrentLocale => _currentLocale;

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableLocales
    {
        get
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in BuiltInLocales) found.Add(b);

            try
            {
                if (Directory.Exists(_localesDir))
                {
                    foreach (var file in Directory.EnumerateFiles(_localesDir, "*.json"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (!string.IsNullOrEmpty(name)) found.Add(name);
                    }
                }
            }
            catch (IOException)
            {
                // best-effort: 目录不可读时仅返回内置 locale。
            }
            catch (UnauthorizedAccessException)
            {
                // best-effort: 权限不足时仅返回内置 locale。
            }

            // 内置项保持固定顺序在前, 用户项按字母序追加。
            var result = new List<string>(found.Count);
            foreach (var b in BuiltInLocales)
            {
                if (found.Remove(b)) result.Add(b);
            }
            foreach (var extra in found.OrderBy(x => x, StringComparer.Ordinal))
            {
                result.Add(extra);
            }
            return result;
        }
    }

    /// <inheritdoc />
    public string Translate(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveTemplate(key);
    }

    /// <inheritdoc />
    public string Translate(string key, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(key);

        var locale = _currentLocale;
        if (!_translations.TryGetValue(locale, out var table)
            || !table.TryGetValue(key, out var template))
        {
            if (!_translations.TryGetValue(DefaultLocale, out var fallback)
                || !fallback.TryGetValue(key, out template))
            {
                // 未找到: 返回 key 本身, 不做格式化。
                return key;
            }
        }

        return args.Length == 0 ? template : string.Format(template, args);
    }

    /// <inheritdoc />
    public void SetLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            throw new ArgumentException("Locale cannot be null or whitespace.", nameof(locale));
        }

        // 仅在该 locale 首次切换时尝试加载用户文件 (缓存)。
        bool needLoad;
        lock (_loadedLock)
        {
            needLoad = _loadedLocales.Add(locale);
        }
        if (needLoad)
        {
            TryLoadUserFile(locale);
        }

        _currentLocale = locale;
        LocaleChanged?.Invoke(this, locale);
    }

    /// <inheritdoc />
    public async Task LoadLocaleAsync(string locale, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;

        var path = Path.Combine(_localesDir, $"{locale}.json");
        if (!File.Exists(path))
        {
            MarkLoaded(locale);
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // best-effort: 读失败视为未加载。
            return;
        }

        MergeUserTranslations(locale, text);
        MarkLoaded(locale);
    }

    /// <inheritdoc />
    public event EventHandler<string>? LocaleChanged;

    /// <summary>解析 key 的翻译模板 (不格式化), 走 fallback 链。</summary>
    private string ResolveTemplate(string key)
    {
        var locale = _currentLocale;
        if (_translations.TryGetValue(locale, out var table)
            && table.TryGetValue(key, out var template))
        {
            return template;
        }
        if (_translations.TryGetValue(DefaultLocale, out var fallback)
            && fallback.TryGetValue(key, out template))
        {
            return template;
        }
        return key;
    }

    /// <summary>同步读取并合并用户 locale 文件。文件缺失或非法时静默降级。</summary>
    private void TryLoadUserFile(string locale)
    {
        var path = Path.Combine(_localesDir, $"{locale}.json");
        if (!File.Exists(path)) return;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return;
        }

        MergeUserTranslations(locale, text);
    }

    /// <summary>
    /// 将用户 JSON 文本合并到指定 locale 的翻译表。
    /// 用户条目覆盖内置同名条目; 非法 JSON 静默降级 (保留内置表)。
    /// </summary>
    private void MergeUserTranslations(string locale, string jsonText)
    {
        Dictionary<string, string> userDict;
        try
        {
            // 不使用 CamelCase / 大小写不敏感策略: 保留 key 原始大小写 (dotted path 字面匹配)。
            userDict = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonText)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // 非法 JSON: 保留内置表, 不抛异常 (graceful degradation)。
            return;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_translations.TryGetValue(locale, out var builtIn))
        {
            foreach (var kv in builtIn) merged[kv.Key] = kv.Value;
        }
        foreach (var kv in userDict) merged[kv.Key] = kv.Value;

        _translations[locale] = merged;
    }

    /// <summary>标记某 locale 已尝试从磁盘加载, 避免后续 SetLocale 重复读盘。</summary>
    private void MarkLoaded(string locale)
    {
        lock (_loadedLock)
        {
            _loadedLocales.Add(locale);
        }
    }

    // ----- 内置翻译表 (覆盖 TUI / GUI / 命令描述 / 错误 / 通用按钮 / 确认提示) -----
    // key 命名规范: tui.* / gui.* / common.* / error.* / confirm.* / commands.* / shell.* / ui.* (兼容旧 key)

    private static Dictionary<string, string> BuiltIn_en_US() => new(StringComparer.Ordinal)
    {
        // --- shell 通用 (兼容旧 key) ---
        ["shell.banner"] = "OpenShell CLI",
        ["shell.prompt"] = "{0}> ",
        // --- TUI ---
        ["tui.banner"] = "OpenShell CLI",
        ["tui.cwd"] = "  cwd: {0}",
        ["tui.providers"] = "  providers: {0}",
        ["tui.commands.count"] = "  commands: {0} registered (try 'get-command')",
        ["tui.help.hint"] = "  type 'help' or 'get-help <command>' for assistance. 'exit' to quit.",
        ["tui.profile.summary"] = "  profile: {0} file(s), {1} line(s) executed.",
        ["tui.warn.config"] = "[warn] failed to load config: {0}",
        ["tui.warn.profile"] = "[warn] profile execution failed: {0}",
        ["tui.items.summary"] = "  -- {0} item(s), {1:N0} bytes",
        ["tui.items.empty"] = "  (empty)",
        ["tui.suspend.maxDepth"] = "Maximum suspend nesting depth reached; resuming.",
        ["tui.suspend.enter"] = "Entering nested REPL. Type 'exit' to resume the suspended operation.",
        ["tui.suspend.error"] = "[suspend] {0}",
        ["tui.lang.ps1"] = "Switched to PowerShell compatibility mode (ps1).",
        ["tui.lang.osh"] = "Switched to modern syntax mode (osh).",
        ["tui.i18n.preloadFailed"] = "[i18n] preload failed: {0}",
        ["tui.plugins.loaded"] = "[plugins] loaded '{0}' v{1}: {2} provider(s), {3} command(s)",
        ["tui.plugins.loadFailed"] = "[plugins] failed to load '{0}': {1}",
        ["tui.plugins.discoveryFailed"] = "[plugins] discovery failed: {0}",
        ["tui.sessions.crash"] = "[sessions] previous session '{0}' did not exit cleanly (pid {1}, machine '{2}'). Starting fresh.",
        ["tui.sessions.running"] = "[sessions] session '{0}' may already be running (pid {1}). Continuing anyway; lock will be overwritten.",
        ["tui.sessions.initFailed"] = "[sessions] failed to initialize session '{0}': {1}",
        ["tui.sessions.saveFailed"] = "[sessions] failed to save/release session '{0}': {1}",
        ["tui.ipc.startFailed"] = "[ipc] server start failed: {0}",
        ["tui.ipc.starting"] = "[ipc] server starting on {0} (protocol v{1})",
        ["tui.fatal"] = "[fatal] {0}",
        ["tui.registryProvider.skipped"] = "RegistryProvider not registered: requires Windows. 'reg::' paths will be unavailable.",
        // --- 确认提示 ---
        ["confirm.title"] = "Confirm",
        ["confirm.areYouSure"] = "Are you sure you want to perform this action?",
        ["confirm.performing"] = "Performing the operation \"{0}\" on target \"{1}\".",
        ["confirm.choices"] = "[Y] Yes  [A] Yes to All  [N] No  [L] No to All  [S] Suspend  [?] Help (default is \"Y\")",
        ["confirm.suspendFailed"] = "Suspend callback failed: {0}",
        ["confirm.resuming"] = "Resuming confirmation prompt.",
        ["confirm.suspendUnavailable"] = "Suspend is not available in this host; defaulting to No.",
        ["confirm.help"] = "Y = Yes, A = Yes to All, N = No, L = No to All, S = Suspend (enter nested REPL)",
        ["confirm.invalidInput"] = "Invalid input. Valid choices are Y / A / N / L / S / ?",
        ["confirm.noInput"] = "No input available (non-interactive mode); defaulting to No.",
        // --- 通用按钮 ---
        ["common.ok"] = "OK",
        ["common.cancel"] = "Cancel",
        ["common.yes"] = "Yes",
        ["common.no"] = "No",
        ["common.details"] = "Details",
        ["common.run"] = "Run",
        ["common.clear"] = "Clear",
        ["common.retry"] = "Retry",
        // --- 错误 ---
        ["error.commandNotFound"] = "command not found: {0}",
        ["error.commandSuggestion"] = "try 'get-command' to list available commands",
        ["error.permission.denied"] = "Permission denied: {0}",
        ["error.invalidArgument"] = "Invalid argument: {0}",
        // --- GUI: MainWindow ---
        ["gui.title"] = "OpenShell",
        ["gui.search.watermark"] = "Search",
        ["gui.address.label"] = "Address",
        ["gui.files.label"] = "Files",
        ["gui.tool.back"] = "Back (Alt+Left)",
        ["gui.tool.forward"] = "Forward (Alt+Right)",
        ["gui.tool.up"] = "Up (Alt+Up)",
        ["gui.tool.refresh"] = "Refresh (F5)",
        ["gui.tool.newFolder"] = "New folder",
        ["gui.tool.copy"] = "Copy (Ctrl+C)",
        ["gui.tool.move"] = "Move (Ctrl+M)",
        ["gui.tool.delete"] = "Delete",
        ["gui.tool.rename"] = "Rename (F2)",
        ["gui.button.new"] = "📁 New",
        ["gui.button.copy"] = "📋 Copy",
        ["gui.button.move"] = "✂ Move",
        ["gui.button.delete"] = "🗑 Delete",
        ["gui.button.rename"] = "✎ Rename",
        ["gui.column.name"] = "Name",
        ["gui.column.size"] = "Size",
        ["gui.column.type"] = "Type",
        ["gui.column.modified"] = "Date modified",
        ["gui.status.items"] = "Items:",
        ["gui.status.selected"] = "Selected:",
        ["gui.status.errors"] = "Errors: {0}",
        ["gui.status.errorsLabel"] = "Errors:",
        ["gui.status.loadingProfile"] = "Loading profile...",
        ["gui.status.showErrors"] = "Show Error Panel",
        ["gui.status.tasks"] = "Tasks: {0}",
        ["gui.status.copiedN"] = "Copied {0} item(s) to {1}",
        ["gui.status.movedN"] = "Moved {0} item(s) to {1}",
        ["gui.errors.title"] = "Errors",
        ["gui.console.input"] = "Command (Ctrl+` to toggle, Enter to run)",
        ["gui.console.title"] = "Command Console (Ctrl+` to toggle)",
        // --- GUI: 菜单 ---
        ["gui.menu.file"] = "File",
        ["gui.menu.edit"] = "Edit",
        ["gui.menu.view"] = "View",
        ["gui.menu.help"] = "Help",
        ["gui.menu.openConsole"] = "Open Console",
        ["gui.menu.newWindow"] = "Open New Window",
        ["gui.menu.newTab"] = "New Tab",
        ["gui.menu.closeTab"] = "Close Tab",
        ["gui.menu.exit"] = "Exit",
        ["gui.menu.copy"] = "Copy",
        ["gui.menu.move"] = "Move",
        ["gui.menu.delete"] = "Delete",
        ["gui.menu.rename"] = "Rename",
        ["gui.menu.selectAll"] = "Select All",
        ["gui.menu.refresh"] = "Refresh",
        ["gui.menu.toggleConsole"] = "Toggle Console",
        ["gui.menu.errorPanel"] = "Error Panel",
        ["gui.menu.theme"] = "Theme",
        ["gui.theme.light"] = "Light",
        ["gui.theme.dark"] = "Dark",
        ["gui.theme.system"] = "System",
        ["gui.menu.viewMode"] = "View Mode",
        ["gui.viewMode.details"] = "Details",
        ["gui.viewMode.icons"] = "Icons",
        ["gui.viewMode.tiles"] = "Tiles",
        ["gui.viewMode.list"] = "List",
        ["gui.menu.about"] = "About OpenShell",
        // --- GUI: 右键菜单 ---
        ["gui.ctx.open"] = "Open",
        ["gui.ctx.copy"] = "Copy",
        ["gui.ctx.move"] = "Move",
        ["gui.ctx.delete"] = "Delete",
        ["gui.ctx.rename"] = "Rename",
        ["gui.ctx.properties"] = "Properties",
        // --- GUI: 导航树 ---
        ["gui.nav.quickAccess"] = "Quick access",
        ["gui.nav.desktop"] = "Desktop",
        ["gui.nav.downloads"] = "Downloads",
        ["gui.nav.documents"] = "Documents",
        ["gui.nav.pictures"] = "Pictures",
        ["gui.nav.thisPc"] = "This PC",
        ["gui.nav.localDisk"] = "Local Disk (C:)",
        ["gui.nav.localDiskD"] = "Local Disk (D:)",
        ["gui.nav.home"] = "Home",
        ["gui.nav.network"] = "Network",
        ["gui.nav.recent"] = "Recent",
        // --- GUI: MainViewModel 对话框 ---
        ["gui.dialog.copyTo"] = "Copy to folder",
        ["gui.dialog.moveTo"] = "Move to folder",
        ["gui.dialog.deleteTitle"] = "Delete",
        ["gui.dialog.deleteMessage"] = "Delete {0} item(s): {1}?",
        ["gui.dialog.cannotOpenNonFs"] = "Cannot open non-fs item: {0}",
        ["gui.dialog.cannotShortcutNonFs"] = "Cannot create shortcut for non-fs item: {0}",
        ["gui.dialog.noSelectionForShortcut"] = "Select an item first to create a shortcut",
        ["gui.dialog.openFailed"] = "Open failed: {0}",
        ["gui.dialog.renameTitle"] = "Rename",
        ["gui.dialog.renameLabel"] = "New name",
        ["gui.dialog.nameEmpty"] = "Name cannot be empty",
        ["gui.dialog.aboutTitle"] = "About OpenShell",
        ["gui.dialog.aboutMessage"] = "OpenShell\nExplorer-style GUI Shell.\n\nADR-0001 through ADR-0059 implemented.",
        ["gui.dialog.propertiesTitle"] = "Properties",
        ["gui.dialog.propName"] = "Name: {0}",
        ["gui.dialog.propPath"] = "Path: {0}",
        ["gui.dialog.propProvider"] = "Provider: {0}",
        ["gui.dialog.propKind"] = "Kind: {0}",
        ["gui.dialog.propSize"] = "Size: {0} bytes",
        ["gui.dialog.propModified"] = "Modified: {0}",
        ["gui.console.ok"] = "(ok)",
        ["gui.console.error"] = "(error) {0}",
        ["gui.profile.failed"] = "profile execution failed: {0}",
        // --- GUI: 对话框服务 ---
        ["gui.dialog.openTitle"] = "Open",
        ["gui.dialog.saveAsTitle"] = "Save As",
        ["gui.dialog.selectFolderTitle"] = "Select Folder",
        // --- GUI: ItemTypeConverter ---
        ["gui.type.folder"] = "File folder",
        ["gui.type.file"] = "File",
        // --- GUI: GlobalSearch ---
        ["gui.search.watermark.full"] = "Search files (Ctrl+Shift+F)...",
        ["gui.search.title"] = "Global Search — OpenShell",
        ["gui.search.notRegistered"] = "search-global command not registered",
        ["gui.search.results"] = "{0} results in {1} ms",
        ["gui.search.cancelled"] = "cancelled ({0} results)",
        ["gui.search.error"] = "Error: {0}",
        // --- GUI: QuickLook ---
        ["gui.quicklook.title"] = "Quick Look — OpenShell",
        ["gui.quicklook.noPreview"] = "(no preview available)",
        ["gui.quicklook.unknownType"] = "(unknown preview type: {0})",
        ["gui.quicklook.pdfPages"] = "PDF (~{0} pages)",
        ["gui.quicklook.noText"] = "(no extractable text)",
        ["gui.quicklook.durationUnknown"] = "duration unknown",
        ["gui.quicklook.metadataUnavailable"] = "(metadata unavailable)",
        ["gui.quicklook.video"] = "Video",
        ["gui.quicklook.imageFailed"] = "Failed to load image: {0}",
        ["gui.quicklook.entries"] = "{0} entries",
        ["gui.quicklook.imageInfo"] = "{0}x{1} PNG ({2} bytes)",
        ["gui.quicklook.truncated"] = " (truncated)",
        ["gui.quicklook.textHeader"] = "[{0}] {1} lines{2}",
        // --- GUI: ProgressDialog ---
        ["gui.progress.completed"] = "Completed successfully",
        ["gui.progress.failed"] = "Failed: {0}",
        ["gui.progress.cancelled"] = "Cancelled",
        ["gui.progress.unknownError"] = "unknown error",
        // --- GUI: T-400~T-413 新增 keys ---
        ["gui.button.cut"] = "✂ Cut",
        ["gui.button.paste"] = "📋 Paste",
        ["gui.button.copyTo"] = "📋 Copy to...",
        ["gui.tool.cut"] = "Cut (Ctrl+X)",
        ["gui.tool.paste"] = "Paste (Ctrl+V)",
        ["gui.tool.copyTo"] = "Copy to folder",
        ["gui.tool.newFile"] = "New file",
        ["gui.ctx.cut"] = "Cut",
        ["gui.ctx.paste"] = "Paste",
        ["gui.ctx.copyAsPath"] = "Copy as path",
        ["gui.ctx.openInNewWindow"] = "Open in new window",
        ["gui.ctx.pinToQuickAccess"] = "Pin to Quick access",
        ["gui.ctx.createShortcut"] = "Create shortcut",
        ["gui.ctx.sort"] = "Sort by",
        ["gui.ctx.sort.name"] = "Name",
        ["gui.ctx.sort.size"] = "Size",
        ["gui.ctx.sort.type"] = "Type",
        ["gui.ctx.sort.modified"] = "Date modified",
        ["gui.ctx.sort.ascending"] = "Ascending",
        ["gui.ctx.sort.descending"] = "Descending",
        ["gui.ctx.deselectAll"] = "Deselect all",
        ["gui.ctx.invertSelection"] = "Invert selection",
        ["gui.dialog.newFolderTitle"] = "New folder",
        ["gui.dialog.newFolderLabel"] = "Folder name",
        ["gui.dialog.newFileTitle"] = "New file",
        ["gui.dialog.newFileLabel"] = "File name",
        ["gui.dialog.copyAsPath"] = "Path copied to clipboard: {0}",
        ["gui.status.selectedSize"] = "Selected size: {0}",
        ["gui.status.freeSpace"] = "Free space: {0}",
        ["gui.empty.folder"] = "This folder is empty",
        ["gui.empty.filter"] = "No items match this search",
        ["gui.loading"] = "Loading...",
        ["gui.theme.light"] = "Light",
        ["gui.theme.dark"] = "Dark",
        ["gui.theme.system"] = "System",
        ["gui.menu.undo"] = "Undo",
        ["gui.menu.redo"] = "Redo",
        ["gui.menu.cut"] = "Cut",
        ["gui.menu.paste"] = "Paste",
        ["gui.menu.deselectAll"] = "Deselect All",
        ["gui.menu.sort"] = "Sort",
        ["gui.menu.viewMode"] = "View Mode",
        ["gui.menu.theme"] = "Theme",
        ["gui.menu.newFolder"] = "New Folder",
        ["gui.menu.newFile"] = "New File",
        ["gui.menu.copyAsPath"] = "Copy as Path",
        // T-443: 命令面板
        ["gui.commandPalette.title"] = "Command Palette",
        ["gui.commandPalette.watermark"] = "Type a command name or keyword...",
        ["gui.commandPalette.empty"] = "No matching commands",
        // T-445: 属性侧边面板
        ["gui.detailsPane.title"] = "Properties",
        ["gui.detailsPane.name"] = "Name",
        ["gui.detailsPane.path"] = "Path",
        ["gui.detailsPane.size"] = "Size",
        ["gui.detailsPane.type"] = "Type",
        ["gui.detailsPane.modified"] = "Modified",
        ["gui.detailsPane.created"] = "Created",
        ["gui.detailsPane.empty"] = "Select an item to view its properties",
        // T-446: 预览侧边面板
        ["gui.previewPane.title"] = "Preview",
        ["gui.menu.previewPane"] = "Preview Pane",
        ["gui.menu.detailsPane"] = "Details Pane",
        // T-450: 新增菜单项
        ["gui.ctx.createShortcut"] = "Create shortcut",
        ["gui.ctx.openWith"] = "Open with...",
        ["gui.ctx.openInNewTab"] = "Open in new tab",
        ["gui.button.newFile"] = "New File",
        ["gui.tool.newFile"] = "New file",
        // T-450: 复制/移动后选中丢失提示
        ["gui.notice.selectionMoved"] = "Selection cleared after {0}",
        ["gui.notice.copied"] = "Copied {0} item(s)",
        ["gui.notice.moved"] = "Moved {0} item(s)",
        // --- 兼容旧 key (ui.* / commands.* / history.* / config.*) ---
        ["ui.tasks.active"] = "Active tasks: {0}",
        ["ui.tasks.none"] = "No active tasks",
        ["ui.menu.file"] = "File",
        ["ui.menu.edit"] = "Edit",
        ["ui.menu.view"] = "View",
        ["ui.menu.help"] = "Help",
        ["history.empty"] = "(no history)",
        ["config.saved"] = "configuration saved",
        ["commands.get-childitem.description"] = "List items in a container",
        ["commands.set-location.description"] = "Change the current location",
        ["commands.copy-item.description"] = "Copy an item to a destination",
        ["commands.move-item.description"] = "Move an item to a new location",
        ["commands.remove-item.description"] = "Remove an item",
        ["commands.set-locale.description"] = "Set the active locale",
    };

    private static Dictionary<string, string> BuiltIn_zh_CN() => new(StringComparer.Ordinal)
    {
        // --- shell 通用 (兼容旧 key) ---
        ["shell.banner"] = "OpenShell 命令行",
        ["shell.prompt"] = "{0}> ",
        // --- TUI ---
        ["tui.banner"] = "OpenShell 命令行",
        ["tui.cwd"] = "  当前路径: {0}",
        ["tui.providers"] = "  提供程序: {0}",
        ["tui.commands.count"] = "  命令: 已注册 {0} 个 (输入 'get-command' 查看)",
        ["tui.help.hint"] = "  输入 'help' 或 'get-help <命令>' 获取帮助。'exit' 退出。",
        ["tui.profile.summary"] = "  profile: 执行了 {0} 个文件, {1} 行。",
        ["tui.warn.config"] = "[警告] 加载配置失败: {0}",
        ["tui.warn.profile"] = "[警告] profile 执行失败: {0}",
        ["tui.items.summary"] = "  -- {0} 项, {1:N0} 字节",
        ["tui.items.empty"] = "  (空)",
        ["tui.suspend.maxDepth"] = "已达挂起最大嵌套深度; 恢复执行。",
        ["tui.suspend.enter"] = "进入嵌套 REPL。输入 'exit' 恢复被挂起的操作。",
        ["tui.suspend.error"] = "[挂起] {0}",
        ["tui.lang.ps1"] = "已切换到 PowerShell 兼容模式 (ps1)。",
        ["tui.lang.osh"] = "已切换到现代语法模式 (osh)。",
        ["tui.i18n.preloadFailed"] = "[i18n] 预加载失败: {0}",
        ["tui.plugins.loaded"] = "[插件] 已加载 '{0}' v{1}: {2} 个提供程序, {3} 个命令",
        ["tui.plugins.loadFailed"] = "[插件] 加载 '{0}' 失败: {1}",
        ["tui.plugins.discoveryFailed"] = "[插件] 发现失败: {0}",
        ["tui.sessions.crash"] = "[会话] 上次会话 '{0}' 未正常退出 (pid {1}, 机器 '{2}')。重新启动。",
        ["tui.sessions.running"] = "[会话] 会话 '{0}' 可能正在运行 (pid {1})。继续执行; 锁将被覆盖。",
        ["tui.sessions.initFailed"] = "[会话] 初始化会话 '{0}' 失败: {1}",
        ["tui.sessions.saveFailed"] = "[会话] 保存/释放会话 '{0}' 失败: {1}",
        ["tui.ipc.startFailed"] = "[ipc] 服务器启动失败: {0}",
        ["tui.ipc.starting"] = "[ipc] 服务器启动于 {0} (协议 v{1})",
        ["tui.fatal"] = "[致命] {0}",
        ["tui.registryProvider.skipped"] = "RegistryProvider 未注册: 需要 Windows。'reg::' 路径将不可用。",
        // --- 确认提示 ---
        ["confirm.title"] = "确认",
        ["confirm.areYouSure"] = "确定要执行此操作吗?",
        ["confirm.performing"] = "正在对目标 \"{1}\" 执行操作 \"{0}\"。",
        ["confirm.choices"] = "[Y] 是  [A] 全是  [N] 否  [L] 全否  [S] 挂起  [?] 帮助 (默认为 \"Y\")",
        ["confirm.suspendFailed"] = "挂起回调失败: {0}",
        ["confirm.resuming"] = "恢复确认提示。",
        ["confirm.suspendUnavailable"] = "此 host 不支持挂起; 默认为否。",
        ["confirm.help"] = "Y = 是, A = 全是, N = 否, L = 全否, S = 挂起 (进入嵌套 REPL)",
        ["confirm.invalidInput"] = "无效输入。有效选项为 Y / A / N / L / S / ?",
        ["confirm.noInput"] = "无可用输入（非交互模式）；默认拒绝操作。",
        // --- 通用按钮 ---
        ["common.ok"] = "确定",
        ["common.cancel"] = "取消",
        ["common.yes"] = "是",
        ["common.no"] = "否",
        ["common.details"] = "详情",
        ["common.run"] = "运行",
        ["common.clear"] = "清除",
        ["common.retry"] = "重试",
        // --- 错误 ---
        ["error.commandNotFound"] = "未找到命令: {0}",
        ["error.commandSuggestion"] = "输入 'get-command' 列出可用命令",
        ["error.permission.denied"] = "权限被拒绝: {0}",
        ["error.invalidArgument"] = "无效参数: {0}",
        // --- GUI: MainWindow ---
        ["gui.title"] = "OpenShell",
        ["gui.search.watermark"] = "搜索",
        ["gui.address.label"] = "地址",
        ["gui.files.label"] = "文件列表",
        ["gui.tool.back"] = "后退 (Alt+Left)",
        ["gui.tool.forward"] = "前进 (Alt+Right)",
        ["gui.tool.up"] = "上一级 (Alt+Up)",
        ["gui.tool.refresh"] = "刷新 (F5)",
        ["gui.tool.newFolder"] = "新建文件夹",
        ["gui.tool.copy"] = "复制 (Ctrl+C)",
        ["gui.tool.move"] = "移动 (Ctrl+M)",
        ["gui.tool.delete"] = "删除",
        ["gui.tool.rename"] = "重命名 (F2)",
        ["gui.button.new"] = "📁 新建",
        ["gui.button.copy"] = "📋 复制",
        ["gui.button.move"] = "✂ 移动",
        ["gui.button.delete"] = "🗑 删除",
        ["gui.button.rename"] = "✎ 重命名",
        ["gui.column.name"] = "名称",
        ["gui.column.size"] = "大小",
        ["gui.column.type"] = "类型",
        ["gui.column.modified"] = "修改日期",
        ["gui.status.items"] = "项数:",
        ["gui.status.selected"] = "选中:",
        ["gui.status.errors"] = "错误: {0}",
        ["gui.status.errorsLabel"] = "错误:",
        ["gui.status.loadingProfile"] = "正在加载 profile...",
        ["gui.status.showErrors"] = "显示错误面板",
        ["gui.status.tasks"] = "任务: {0}",
        ["gui.status.copiedN"] = "已复制 {0} 项到 {1}",
        ["gui.status.movedN"] = "已移动 {0} 项到 {1}",
        ["gui.errors.title"] = "错误",
        ["gui.console.input"] = "命令 (Ctrl+` 切换, Enter 执行)",
        ["gui.console.title"] = "命令控制台 (Ctrl+` 切换)",
        // --- GUI: 菜单 ---
        ["gui.menu.file"] = "文件",
        ["gui.menu.edit"] = "编辑",
        ["gui.menu.view"] = "查看",
        ["gui.menu.help"] = "帮助",
        ["gui.menu.openConsole"] = "打开控制台",
        ["gui.menu.newWindow"] = "打开新窗口",
        ["gui.menu.newTab"] = "新建标签页",
        ["gui.menu.closeTab"] = "关闭标签页",
        ["gui.menu.exit"] = "退出",
        ["gui.menu.copy"] = "复制",
        ["gui.menu.move"] = "移动",
        ["gui.menu.delete"] = "删除",
        ["gui.menu.rename"] = "重命名",
        ["gui.menu.selectAll"] = "全选",
        ["gui.menu.refresh"] = "刷新",
        ["gui.menu.toggleConsole"] = "切换控制台",
        ["gui.menu.errorPanel"] = "错误面板",
        ["gui.menu.theme"] = "主题",
        ["gui.theme.light"] = "浅色",
        ["gui.theme.dark"] = "深色",
        ["gui.theme.system"] = "跟随系统",
        ["gui.menu.viewMode"] = "视图模式",
        ["gui.viewMode.details"] = "详细信息",
        ["gui.viewMode.icons"] = "大图标",
        ["gui.viewMode.tiles"] = "平铺",
        ["gui.viewMode.list"] = "列表",
        ["gui.menu.about"] = "关于 OpenShell",
        // --- GUI: 右键菜单 ---
        ["gui.ctx.open"] = "打开",
        ["gui.ctx.copy"] = "复制",
        ["gui.ctx.move"] = "移动",
        ["gui.ctx.delete"] = "删除",
        ["gui.ctx.rename"] = "重命名",
        ["gui.ctx.properties"] = "属性",
        // --- GUI: 导航树 ---
        ["gui.nav.quickAccess"] = "快速访问",
        ["gui.nav.desktop"] = "桌面",
        ["gui.nav.downloads"] = "下载",
        ["gui.nav.documents"] = "文档",
        ["gui.nav.pictures"] = "图片",
        ["gui.nav.thisPc"] = "此电脑",
        ["gui.nav.localDisk"] = "本地磁盘 (C:)",
        ["gui.nav.localDiskD"] = "本地磁盘 (D:)",
        ["gui.nav.home"] = "主目录",
        ["gui.nav.network"] = "网络",
        ["gui.nav.recent"] = "最近访问",
        // --- GUI: MainViewModel 对话框 ---
        ["gui.dialog.copyTo"] = "复制到文件夹",
        ["gui.dialog.moveTo"] = "移动到文件夹",
        ["gui.dialog.deleteTitle"] = "删除",
        ["gui.dialog.deleteMessage"] = "删除 {0} 项: {1}?",
        ["gui.dialog.cannotOpenNonFs"] = "无法打开非文件系统项: {0}",
        ["gui.dialog.cannotShortcutNonFs"] = "无法为非文件系统项创建快捷方式: {0}",
        ["gui.dialog.noSelectionForShortcut"] = "请先选中一个项以创建快捷方式",
        ["gui.dialog.openFailed"] = "打开失败: {0}",
        ["gui.dialog.renameTitle"] = "重命名",
        ["gui.dialog.renameLabel"] = "新名称",
        ["gui.dialog.nameEmpty"] = "名称不能为空",
        ["gui.dialog.aboutTitle"] = "关于 OpenShell",
        ["gui.dialog.aboutMessage"] = "OpenShell\n资源管理器风格 GUI Shell。\n\nADR-0001 至 ADR-0059 已实现。",
        ["gui.dialog.propertiesTitle"] = "属性",
        ["gui.dialog.propName"] = "名称: {0}",
        ["gui.dialog.propPath"] = "路径: {0}",
        ["gui.dialog.propProvider"] = "提供程序: {0}",
        ["gui.dialog.propKind"] = "类型: {0}",
        ["gui.dialog.propSize"] = "大小: {0} 字节",
        ["gui.dialog.propModified"] = "修改时间: {0}",
        ["gui.console.ok"] = "(成功)",
        ["gui.console.error"] = "(错误) {0}",
        ["gui.profile.failed"] = "profile 执行失败: {0}",
        // --- GUI: 对话框服务 ---
        ["gui.dialog.openTitle"] = "打开",
        ["gui.dialog.saveAsTitle"] = "另存为",
        ["gui.dialog.selectFolderTitle"] = "选择文件夹",
        // --- GUI: ItemTypeConverter ---
        ["gui.type.folder"] = "文件夹",
        ["gui.type.file"] = "文件",
        // --- GUI: GlobalSearch ---
        ["gui.search.watermark.full"] = "搜索文件 (Ctrl+Shift+F)...",
        ["gui.search.title"] = "全局搜索 — OpenShell",
        ["gui.search.notRegistered"] = "search-global 命令未注册",
        ["gui.search.results"] = "{0} 个结果, 耗时 {1} 毫秒",
        ["gui.search.cancelled"] = "已取消 ({0} 个结果)",
        ["gui.search.error"] = "错误: {0}",
        // --- GUI: QuickLook ---
        ["gui.quicklook.title"] = "快速预览 — OpenShell",
        ["gui.quicklook.noPreview"] = "(无预览可用)",
        ["gui.quicklook.unknownType"] = "(未知预览类型: {0})",
        ["gui.quicklook.pdfPages"] = "PDF (约 {0} 页)",
        ["gui.quicklook.noText"] = "(无可提取文本)",
        ["gui.quicklook.durationUnknown"] = "时长未知",
        ["gui.quicklook.metadataUnavailable"] = "(元数据不可用)",
        ["gui.quicklook.video"] = "视频",
        ["gui.quicklook.imageFailed"] = "加载图片失败: {0}",
        ["gui.quicklook.entries"] = "{0} 个条目",
        ["gui.quicklook.imageInfo"] = "{0}x{1} PNG ({2} 字节)",
        ["gui.quicklook.truncated"] = " (已截断)",
        ["gui.quicklook.textHeader"] = "[{0}] {1} 行{2}",
        // --- GUI: ProgressDialog ---
        ["gui.progress.completed"] = "已完成",
        ["gui.progress.failed"] = "失败: {0}",
        ["gui.progress.cancelled"] = "已取消",
        ["gui.progress.unknownError"] = "未知错误",
        // --- GUI: T-400~T-413 新增 keys ---
        ["gui.button.cut"] = "✂ 剪切",
        ["gui.button.paste"] = "📋 粘贴",
        ["gui.button.copyTo"] = "📋 复制到...",
        ["gui.tool.cut"] = "剪切 (Ctrl+X)",
        ["gui.tool.paste"] = "粘贴 (Ctrl+V)",
        ["gui.tool.copyTo"] = "复制到文件夹",
        ["gui.tool.newFile"] = "新建文件",
        ["gui.ctx.cut"] = "剪切",
        ["gui.ctx.paste"] = "粘贴",
        ["gui.ctx.copyAsPath"] = "复制路径",
        ["gui.ctx.openInNewWindow"] = "在新窗口打开",
        ["gui.ctx.pinToQuickAccess"] = "固定到快速访问",
        ["gui.ctx.createShortcut"] = "创建快捷方式",
        ["gui.ctx.sort"] = "排序方式",
        ["gui.ctx.sort.name"] = "名称",
        ["gui.ctx.sort.size"] = "大小",
        ["gui.ctx.sort.type"] = "类型",
        ["gui.ctx.sort.modified"] = "修改日期",
        ["gui.ctx.sort.ascending"] = "升序",
        ["gui.ctx.sort.descending"] = "降序",
        ["gui.ctx.deselectAll"] = "取消全选",
        ["gui.ctx.invertSelection"] = "反向选择",
        ["gui.dialog.newFolderTitle"] = "新建文件夹",
        ["gui.dialog.newFolderLabel"] = "文件夹名称",
        ["gui.dialog.newFileTitle"] = "新建文件",
        ["gui.dialog.newFileLabel"] = "文件名称",
        ["gui.dialog.copyAsPath"] = "路径已复制到剪贴板: {0}",
        ["gui.status.selectedSize"] = "选中大小: {0}",
        ["gui.status.freeSpace"] = "可用空间: {0}",
        ["gui.empty.folder"] = "此文件夹为空",
        ["gui.empty.filter"] = "没有符合当前搜索的项目",
        ["gui.loading"] = "加载中...",
        ["gui.theme.light"] = "浅色",
        ["gui.theme.dark"] = "深色",
        ["gui.theme.system"] = "跟随系统",
        ["gui.menu.undo"] = "撤销",
        ["gui.menu.redo"] = "重做",
        ["gui.menu.cut"] = "剪切",
        ["gui.menu.paste"] = "粘贴",
        ["gui.menu.deselectAll"] = "取消全选",
        ["gui.menu.sort"] = "排序",
        ["gui.menu.viewMode"] = "视图模式",
        ["gui.menu.theme"] = "主题",
        ["gui.menu.newFolder"] = "新建文件夹",
        ["gui.menu.newFile"] = "新建文件",
        ["gui.menu.copyAsPath"] = "复制路径",
        // T-443: 命令面板
        ["gui.commandPalette.title"] = "命令面板",
        ["gui.commandPalette.watermark"] = "输入命令名或关键词...",
        ["gui.commandPalette.empty"] = "无匹配命令",
        // T-445: 属性侧边面板
        ["gui.detailsPane.title"] = "属性",
        ["gui.detailsPane.name"] = "名称",
        ["gui.detailsPane.path"] = "路径",
        ["gui.detailsPane.size"] = "大小",
        ["gui.detailsPane.type"] = "类型",
        ["gui.detailsPane.modified"] = "修改时间",
        ["gui.detailsPane.created"] = "创建时间",
        ["gui.detailsPane.empty"] = "选择一个项目以查看属性",
        // T-446: 预览侧边面板
        ["gui.previewPane.title"] = "预览",
        ["gui.menu.previewPane"] = "预览面板",
        ["gui.menu.detailsPane"] = "属性面板",
        // T-450: 新增菜单项
        ["gui.ctx.createShortcut"] = "创建快捷方式",
        ["gui.ctx.openWith"] = "打开方式...",
        ["gui.ctx.openInNewTab"] = "在新标签页打开",
        ["gui.button.newFile"] = "新建文件",
        ["gui.tool.newFile"] = "新建文件",
        // T-450: 复制/移动后选中丢失提示
        ["gui.notice.selectionMoved"] = "{0} 后选中已清除",
        ["gui.notice.copied"] = "已复制 {0} 项",
        ["gui.notice.moved"] = "已移动 {0} 项",
        // --- 兼容旧 key (ui.* / commands.* / history.* / config.*) ---
        ["ui.tasks.active"] = "活动任务: {0}",
        ["ui.tasks.none"] = "无活动任务",
        ["ui.menu.file"] = "文件",
        ["ui.menu.edit"] = "编辑",
        ["ui.menu.view"] = "查看",
        ["ui.menu.help"] = "帮助",
        ["history.empty"] = "(无历史记录)",
        ["config.saved"] = "配置已保存",
        ["commands.get-childitem.description"] = "列出容器中的项",
        ["commands.set-location.description"] = "更改当前位置",
        ["commands.copy-item.description"] = "将项复制到目标",
        ["commands.move-item.description"] = "将项移动到新位置",
        ["commands.remove-item.description"] = "删除项",
        ["commands.set-locale.description"] = "设置当前语言",
    };

    private static Dictionary<string, string> BuiltIn_ja_JP() => new(StringComparer.Ordinal)
    {
        // --- shell 通用 (兼容旧 key) ---
        ["shell.banner"] = "OpenShell CLI",
        ["shell.prompt"] = "{0}> ",
        // --- TUI (ja-JP 仅翻译关键 key, 其余回退 en-US) ---
        ["tui.banner"] = "OpenShell CLI",
        // --- 确认提示 ---
        ["confirm.title"] = "確認",
        ["confirm.areYouSure"] = "この操作を実行してもよろしいですか?",
        // --- 通用按钮 ---
        ["common.ok"] = "OK",
        ["common.cancel"] = "キャンセル",
        ["common.yes"] = "はい",
        // common.no 故意缺失, 用于验证 fallback 链: ja-JP 缺失 → 回退 en-US。
        // --- 错误 ---
        ["error.commandNotFound"] = "コマンドが見つかりません: {0}",
        // --- GUI (ja-JP 回退 en-US, 仅翻译菜单) ---
        ["gui.menu.file"] = "ファイル",
        ["gui.menu.edit"] = "編集",
        ["gui.menu.view"] = "表示",
        ["gui.menu.help"] = "ヘルプ",
        // --- 兼容旧 key ---
        ["ui.tasks.active"] = "アクティブなタスク: {0}",
        ["ui.tasks.none"] = "アクティブなタスクなし",
        ["ui.menu.file"] = "ファイル",
        ["ui.menu.edit"] = "編集",
        ["ui.menu.view"] = "表示",
        ["ui.menu.help"] = "ヘルプ",
        ["history.empty"] = "(履歴なし)",
        ["config.saved"] = "設定を保存しました",
        ["commands.get-childitem.description"] = "コンテナ内の項目を一覧表示",
        ["commands.set-location.description"] = "現在位置を変更",
        ["commands.copy-item.description"] = "項目をコピー先にコピー",
        ["commands.move-item.description"] = "項目を新しい場所に移動",
        ["commands.remove-item.description"] = "項目を削除",
        ["commands.set-locale.description"] = "アクティブなロケールを設定",
        ["error.permission.denied"] = "アクセスが拒否されました: {0}",
        ["error.invalidArgument"] = "無効な引数: {0}",
    };
}
