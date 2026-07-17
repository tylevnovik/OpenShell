# i18n 改造审计报告

- **主题**: GUI / TUI 国际化改造（中文支持）
- **关联 ADR**: ADR-0035（国际化）
- **日期**: 2026-07-11
- **状态**: 已修复（T-300~T-315 全部完成）

---

## 1. 现状概览

### 1.1 已有基础设施

| 组件 | 位置 | 状态 |
|------|------|------|
| `II18nService` 接口 | `src/OpenShell.Core/I18n/II18nService.cs` | 已实现：Translate / SetLocale / LoadLocaleAsync / LocaleChanged |
| `ResourceI18nService` 实现 | `src/OpenShell.Core/I18n/ResourceI18nService.cs` | 已实现：JSON 资源 + 用户文件合并 + fallback 链 |
| 内置 locale | en-US / zh-CN / ja-JP | 仅约 20 个 key（shell.banner / 命令描述 / 菜单 / 错误 / 通用按钮） |
| DI 注册 | CLI `Program.cs` + GUI `AppBuilder.cs` | 两个 host 均已注册 `II18nService` 单例 |
| `set-locale` 命令 | `SetLocaleCommand.cs` | 已实现：切换 locale + 输出确认 |

### 1.2 核心缺陷

**`II18nService.Translate()` 从未被 GUI / TUI 代码调用。** i18n 基础设施虽已注册，但所有用户可见字符串仍为硬编码英文，未接入翻译服务。具体分布见下文 §2。

### 1.3 默认 locale

当前 `ResourceI18nService.DefaultLocale = "en-US"`，`_currentLocale` 初始值为 en-US。用户要求默认 zh-CN。

---

## 2. 硬编码字符串清单（按文件）

### 2.1 TUI — `src/OpenShell.Cli.Host/Program.cs`

| 严重度 | 字符串示例 | 位置 | 说明 |
|--------|-----------|------|------|
| 高 | `"OpenShell CLI"` | `Ansi.WriteBanner` L1491-1492 | 启动横幅 |
| 高 | `"  cwd: {0}"` | `RunAsync` L754 | 当前路径提示 |
| 高 | `"  providers: {0}"` | L755 | provider 列表 |
| 高 | `"  commands: {0} registered (try 'get-command')"` | L756 | 命令计数 |
| 高 | `"  type 'help' or 'get-help <command>' for assistance. 'exit' to quit."` | L757 | 帮助提示 |
| 中 | `"  profile: {0} file(s), {1} line(s) executed."` | L772 | profile 执行摘要 |
| 中 | `"[warn] failed to load config: {0}"` | L751 | 配置加载失败 |
| 中 | `"[warn] profile execution failed: {0}"` | L783 | profile 执行失败 |
| 中 | `"  -- {0} item(s), {1:N0} bytes"` | L724 | 列表项统计 |
| 中 | `"  (empty)"` | L728 | 空列表 |
| 中 | `"Maximum suspend nesting depth reached; resuming."` | L613 | 挂起深度上限 |
| 中 | `"Entering nested REPL. Type 'exit' to resume the suspended operation."` | L619 | 嵌套 REPL 入口 |
| 中 | `"Switched to PowerShell compatibility mode (ps1)."` | L1069 | 语法切换 |
| 中 | `"Switched to modern syntax mode (osh)."` | L1075 | 语法切换 |
| 中 | `"command not found: {0}"` | L1002 | 命令未找到 |
| 中 | `"try 'get-command' to list available commands"` | L1005 | 建议 |
| 低 | `"[i18n] preload failed: {0}"` | L332 | i18n 预加载失败 |
| 低 | `"[plugins] loaded '{0}' v{1}: ..."` | L352 | 插件加载 |
| 低 | `"[plugins] failed to load '{0}': {1}"` | L358 | 插件失败 |
| 低 | `"[plugins] discovery failed: {0}"` | L367 | 插件发现失败 |
| 低 | `"[sessions] previous session '{0}' did not exit cleanly..."` | L414-415 | 会话崩溃 |
| 低 | `"[sessions] session '{0}' may already be running..."` | L420-421 | 会话占用 |
| 低 | `"[sessions] failed to initialize session '{0}': {1}"` | L428 | 会话初始化失败 |
| 低 | `"[sessions] failed to save/release session '{0}': {1}"` | L481 | 会话保存失败 |
| 低 | `"[ipc] server start failed: {0}"` | L452 | IPC 启动失败 |
| 低 | `"[ipc] server starting on {0} (protocol v{1})"` | L454 | IPC 启动 |
| 低 | `"[fatal] {0}"` | L468 | 致命错误 |
| 低 | `"RegistryProvider not registered: requires Windows..."` | L657 | RegistryProvider 跳过 |

### 2.2 TUI — `src/OpenShell.Core/Commands/IConfirmationPrompter.cs`（`ConsoleConfirmationPrompter`）

| 严重度 | 字符串 | 行 | 说明 |
|--------|--------|----|------|
| 高 | `"Confirm"` | L102 | 确认标题 |
| 高 | `"Are you sure you want to perform this action?"` | L103 | 确认提示 |
| 高 | `"Performing the operation \"{0}\" on target \"{1}\"."` | L104 | 操作描述 |
| 高 | `"[Y] Yes  [A] Yes to All  [N] No  [L] No to All  [S] Suspend  [?] Help (default is \"Y\")"` | L105 | 选项 |
| 中 | `"Suspend callback failed: {0}"` | L131 | 挂起失败 |
| 中 | `"Resuming confirmation prompt."` | L135 | 恢复提示 |
| 中 | `"Suspend is not available in this host; defaulting to No."` | L140 | 挂起不可用 |
| 中 | `"Y = Yes, A = Yes to All, ..."` | L143 | 帮助 |
| 中 | `"Invalid input. Valid choices are Y / A / N / L / S / ?"` | L146 | 无效输入 |

### 2.3 GUI — `src/OpenShell.Gui.Host/Views/MainWindow.cs`

| 严重度 | 类别 | 字符串示例 | 说明 |
|--------|------|-----------|------|
| 高 | 标题 | `"OpenShell"` | 窗口标题 |
| 高 | 搜索 | `"Search"` | 搜索框 watermark |
| 高 | 工具提示 | `"Back (Alt+Left)"` 等 9 项 | tooltip |
| 高 | 工具按钮 | `"📁 New"` / `"📋 Copy"` 等 5 项 | 按钮文字 |
| 高 | 列头 | `"Name"` / `"Size"` / `"Type"` / `"Date modified"` | 文件列表列头 |
| 高 | 状态栏 | `"Loading profile..."` / `"Errors: {0}"` / `"Items:"` / `"Selected:"` | 状态栏 |
| 高 | 菜单 | `"_File"` / `"Open _Console"` 等 16 项 | 菜单项 |
| 高 | 右键菜单 | `"_Open"` / `"_Properties"` 等 6 项 | 上下文菜单 |
| 高 | 导航树 | `"Quick access"` / `"Desktop"` / `"This PC"` / `"Network"` 等 9 项 | 导航树节点 |
| 中 | 错误面板 | `"Errors"` / `"Clear"` | 错误面板 |
| 中 | 控制台 | `"Command (Ctrl+\` to toggle, Enter to run)"` / `"Run"` / `"Command Console..."` | 控制台面板 |
| 中 | 状态栏提示 | `"Show Error Panel"` | tooltip |

### 2.4 GUI — `src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 高 | `"Copy to folder"` / `"Move to folder"` | 文件夹选择器标题 |
| 高 | `"Delete"` / `"Delete {0} item(s): {1}?"` | 删除确认 |
| 高 | `"Cannot open non-fs item: {0}"` / `"Open failed: {0}"` | 打开失败 |
| 高 | `"Rename"` / `"New name"` / `"Name cannot be empty"` | 重命名对话框 |
| 高 | `"About OpenShell"` / `"OpenShell\nExplorer-style GUI Shell..."` | 关于对话框 |
| 中 | `"(ok)"` / `"(error) {0}"` | 控制台输出标记 |
| 中 | `"Properties"` + 属性标签 6 项 | 属性对话框 |

### 2.5 GUI — `src/OpenShell.Gui.Host/ViewModels/StatusbarViewModel.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"Tasks: {0}"` | 状态栏任务标签 |

### 2.6 GUI — `src/OpenShell.Gui.Host/Services/MessageBoxWindow.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 高 | `"OK"` / `"Cancel"` / `"Yes"` / `"No"` | 按钮标签 |
| 中 | `"Details"` | 折叠区标题 |

### 2.7 GUI — `src/OpenShell.Gui.Host/Services/InputDialogWindow.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 高 | `"OK"` / `"Cancel"` | 按钮标签 |

### 2.8 GUI — `src/OpenShell.Gui.Host/Services/AvaloniaDialogService.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"Open"` / `"Save As"` / `"Select Folder"` | 默认对话框标题 |

### 2.9 GUI — `src/OpenShell.Gui.Host/Converters/ItemTypeConverter.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"File folder"` / `"File"` | 类型显示 |

### 2.10 GUI — `src/OpenShell.Gui.Host/Views/GlobalSearchWindow.cs` + `ViewModels/GlobalSearchViewModel.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"Search files (Ctrl+Shift+F)..."` | 搜索框 watermark |
| 中 | `"Global Search — OpenShell"` | 窗口标题 |
| 中 | `"search-global command not registered"` | 状态文本 |
| 中 | `"{0} results in {1} ms"` / `"cancelled ({0} results)"` / `"Error: {0}"` | 状态文本 |

### 2.11 GUI — `src/OpenShell.Gui.Host/Views/QuickLookWindow.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"Quick Look — OpenShell"` | 窗口标题 |
| 中 | `"(no preview available)"` / `"(unknown preview type: {0})"` | 预览占位 |
| 中 | `"PDF (~{0} pages)"` / `"(no extractable text)"` | PDF 预览 |
| 中 | `"duration unknown"` / `"(metadata unavailable)"` / `"Video"` | 视频预览 |
| 低 | `"⚠ {0}"` | 不支持预览 |

### 2.12 GUI — `src/OpenShell.Gui.Host/ViewModels/ProgressDialogViewModel.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"Completed successfully"` / `"Cancelled"` | 结果消息 |
| 中 | `"Failed: {0}"` / `"unknown error"` | 失败消息 |

### 2.13 GUI — `src/OpenShell.Gui.Host/GuiHost.cs`

| 严重度 | 字符串 | 说明 |
|--------|--------|------|
| 中 | `"command not found: {0}"` / `"try 'get-command' to list available commands"` | 与 CliHost 共享 |

---

## 3. 架构决策

### 3.1 默认 locale

改为 `zh-CN`。fallback 链保持 `zh-CN → en-US → key 本身`。用户可通过 `set-locale en-US` 切回英文。

### 3.2 GUI 动态切换

`MainWindow` 注入 `II18nService`，订阅 `LocaleChanged` 事件，切换后调用 `ApplyTranslations()` 刷新所有 UI 元素。ViewModel 中运行时生成的字符串（对话框标题、确认消息）在调用时即时翻译，无需缓存。`StatusbarViewModel.TasksLabel` 订阅 `LocaleChanged` 重新 raise。

### 3.3 ItemTypeConverter 静态注入

`ItemTypeConverter` 是无状态值转换器（XAML binding），无法通过 DI 注入。采用静态属性 `I18nAccessor.Instance` 模式：App 启动时设置，转换器读取。避免破坏现有 binding。

### 3.4 命令名不翻译

按 ADR-0035 §10：命令名（`get-childitem`）保持英文，仅翻译 `Description` / `HelpText` / UI 标签。

---

## 4. 缺陷编号

| ID | 文件 | 描述 | 严重度 |
|----|------|------|--------|
| D-300 | ResourceI18nService.cs | 内置翻译表仅约 20 key，远不足以覆盖 GUI/TUI 全部字符串 | 高 |
| D-301 | ResourceI18nService.cs | 默认 locale 为 en-US，不符合中文支持诉求 | 高 |
| D-302 | Program.cs (CliHost) | TUI 所有用户可见字符串硬编码英文，未调用 Translate | 高 |
| D-303 | IConfirmationPrompter.cs | ConsoleConfirmationPrompter 确认提示硬编码英文 | 高 |
| D-304 | MainWindow.cs | GUI 所有 UI 字符串硬编码英文，无动态切换 | 高 |
| D-305 | MainViewModel.cs | 对话框标题/消息硬编码英文 | 高 |
| D-306 | StatusbarViewModel.cs | TasksLabel 硬编码 | 中 |
| D-307 | MessageBoxWindow.cs | 按钮标签硬编码 | 高 |
| D-308 | InputDialogWindow.cs | 按钮标签硬编码 | 高 |
| D-309 | AvaloniaDialogService.cs | 默认标题硬编码 | 中 |
| D-310 | ItemTypeConverter.cs | 类型文字硬编码 | 中 |
| D-311 | GlobalSearchWindow.cs + ViewModel | 搜索窗口字符串硬编码 | 中 |
| D-312 | QuickLookWindow.cs | 预览窗口字符串硬编码 | 中 |
| D-313 | ProgressDialogViewModel.cs | 结果消息硬编码 | 中 |
| D-314 | GuiHost.cs | 命令未找到消息硬编码 | 中 |

---

## 5. 修复总结

### 5.1 修复状态

全部 D-300~D-314 缺陷已修复。对应任务 T-300~T-315 全部完成。

| 任务 | 缺陷 | 修复内容 | 验证 |
|------|------|----------|------|
| T-300 | D-300 | 扩充内置翻译表至约 120 个 key（tui.* / gui.* / common.* / error.* / confirm.*） | `All_EnUs_Keys_Have_ZhCN_Translation` 通过 |
| T-301 | D-301 | StartupLocale 改为 zh-CN，DefaultLocale 保持 en-US 作为 fallback 源 | `Default_Locale_Is_ZhCN` 通过 |
| T-302 | D-302 | CliHost + Ansi + Program.Main 全部 TUI 字符串接入 i18n（约 25 处替换） | `Tui_Banner_Translates_To_Chinese` 通过 |
| T-303 | D-303 | ConsoleConfirmationPrompter 接入 i18n（CLI + GUI 两侧 DI 注册） | `Confirm_Prompt_Translates_To_Chinese` 通过 |
| T-304 | D-314 | GuiHost command not found 消息接入 i18n | `error.commandNotFound` key 验证通过 |
| T-305 | D-304 | MainWindow 全面 i18n 改造 + 动态切换（ApplyTranslations + LocaleChanged 订阅 + Tag-based 菜单绑定） | `MainWindow_Applies_Chinese_Translations` 通过 |
| T-306 | D-305 | MainViewModel 17 处对话框消息接入 i18n | `MainViewModel_Dialogs_Translate_To_Chinese` 通过 |
| T-307 | D-306 | StatusbarViewModel TasksLabel 接入 i18n + LocaleChanged 刷新 | `Statusbar_TasksLabel_Translates` 通过 |
| T-308 | D-307 | MessageBoxWindow 按钮标签接入 i18n + 动态刷新 | `MessageBox_Buttons_Translate` 通过 |
| T-309 | D-308 | InputDialogWindow 按钮标签接入 i18n + 动态刷新 | `InputDialog_Buttons_Translate` 通过 |
| T-310 | D-309 | AvaloniaDialogService 默认标题接入 i18n | key 翻译验证通过 |
| T-311 | D-310 | ItemTypeConverter 接入 I18nAccessor 静态访问器 | key 翻译验证通过 |
| T-312 | D-311 | GlobalSearchWindow + ViewModel 接入 i18n + LocaleChanged 刷新 | key 翻译验证通过 |
| T-313 | D-312 | QuickLookWindow 接入 i18n + LocaleChanged 刷新 | key 翻译验证通过 |
| T-314 | D-313 | ProgressDialogViewModel 结果消息接入 i18n | key 翻译验证通过 |
| T-315 | — | 全量构建 + 测试验证 | 0 警告 0 错误 / 1986 通过 / 7 跳过 / 0 失败 |

### 5.2 验证结果

- `dotnet build OpenShell.slnx`：0 警告 0 错误
- 全解决方案测试：1986 通过 / 7 跳过 / 0 失败
- I18nComplianceTests：14 通过 / 0 跳过 / 0 失败
- 剩余 7 个跳过为预存在的非 i18n 测试（FileSystem 1 + Remote 3 + Core 3）

### 5.3 关键设计决策

1. **默认 locale = zh-CN**：启动即中文界面，DefaultLocale 保持 en-US 作为 fallback 源语言
2. **GUI 动态切换**：通过 `LocaleChanged` 事件 + `ApplyTranslations()` 方法实现，set-locale 后界面立即刷新
3. **Tag-based 菜单绑定**：MenuItem.Tag 存储 i18n key，BindMenuCommands 用 Tag 匹配（避免翻译后 header 匹配失败）
4. **I18nAccessor 静态类**：供无法通过 DI 注入的组件（如 Avalonia IValueConverter）访问 II18nService
5. **Program.Services null 安全**：测试环境 Program.Services 可能为 null，所有 `GetService` 调用使用 null 条件运算符
