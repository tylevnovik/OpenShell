# i18n 改造任务清单

- **主题**: GUI / TUI 国际化改造（中文支持）
- **关联审计**: `docs/i18n-audit.md`
- **关联 ADR**: ADR-0035
- **合规测试**: `tests/OpenShell.Core.Tests/I18n/I18nComplianceTests.cs`

状态标记：`[ ]` 待办 / `[~]` 进行中 / `[x]` 完成 / `[!]` 阻塞

---

## 阶段一：基础设施

### T-300 扩充内置翻译表（D-300）
- [x] 在 `ResourceI18nService.cs` 的 `BuiltIn_en_US()` / `BuiltIn_zh_CN()` 中新增全部 GUI/TUI key（约 120 个）
- [x] key 命名规范：`tui.*`（终端）/ `gui.*`（图形界面）/ `common.*`（通用按钮）/ `error.*`（错误）/ `confirm.*`（确认提示）
- [x] en-US 表为英文模板，zh-CN 表为中文翻译，两者 key 集合一致
- [x] ja-JP 表保持现有 key 不变（新增 key 由 fallback 链回退 en-US）
- [x] 测试：`I18nComplianceTests.All_BuiltIn_Keys_Have_ZhCN_Translation` 通过

### T-301 默认 locale 改为 zh-CN（D-301）
- [x] `ResourceI18nService` 新增 `StartupLocale = "zh-CN"`，`_currentLocale` 初始值改为 `StartupLocale`
- [x] `DefaultLocale` 保持 `"en-US"`（作为 fallback 源语言，避免英文/日文用户看到中文回退）
- [x] `BuiltInLocales` 顺序调整为 `{ "zh-CN", "en-US", "ja-JP" }`（zh-CN 排首位）
- [x] 更新 `ResourceI18nServiceTests.Default_Locale_Is_EnUs` → 重命名为 `Startup_Locale_Is_ZhCN`，断言改为 zh-CN
- [x] 更新受影响的测试（`Translate_KnownKey_EnUs` / `Translate_WithFormatArgs` / `LoadLocaleAsync` / `AvailableLocales`）
- [x] 测试：`I18nComplianceTests.Default_Locale_Is_ZhCN` 通过

---

## 阶段二：TUI i18n

### T-302 CliHost + Ansi 字符串接入 i18n（D-302）
- [x] `CliHost` 构造函数注入 `II18nService`
- [x] `Ansi.WriteBanner` 接受 i18n 参数，翻译 banner
- [x] `RunAsync` 中 cwd / providers / commands / help 提示改用 `Translate`
- [x] profile 执行摘要、warn 消息、item 统计、empty 改用 `Translate`
- [x] 语法切换提示（`Switched to ... mode`）改用 `Translate`
- [x] 命令未找到消息 + 建议改用 `Translate`
- [x] Program.cs `Main` 中插件 / 会话 / IPC / fatal 消息改用 `Translate`
- [x] RegistryProvider 跳过日志改用 `Translate`
- [x] 嵌套 REPL 消息改用 `Translate`
- [x] 测试：`I18nComplianceTests.Tui_Banner_Translates_To_Chinese` 通过

### T-303 ConsoleConfirmationPrompter 接入 i18n（D-303）
- [x] `ConsoleConfirmationPrompter` 构造函数新增 `II18nService?` 可选参数（保持向后兼容）
- [x] Confirm / Are you sure / Performing operation / 选项列表 改用 `Translate`
- [x] Suspend 相关消息改用 `Translate`
- [x] 帮助 / 无效输入消息改用 `Translate`
- [x] CLI `Program.cs` 注册时传入 `II18nService`（GUI `AppBuilder.cs` 待 T-304 阶段同步）
- [x] 测试：`I18nComplianceTests.Confirm_Prompt_Translates_To_Chinese` 通过

### T-304 GuiHost 命令未找到消息接入 i18n（D-314）
- [x] `GuiHost` 构造函数注入 `II18nService`
- [x] `DispatchAsync` 中 command not found 消息 + 建议改用 `Translate`
- [x] GUI `AppBuilder.cs` 注册 `ConsoleConfirmationPrompter` 时传入 `II18nService`（T-303 GUI 部分）
- [x] 测试：`I18nComplianceTests` 中 `error.commandNotFound` key 翻译验证通过（zh-CN: "未找到命令: {0}"）

---

## 阶段三：GUI i18n

### T-305 MainWindow 接入 i18n + 动态切换（D-304）
- [x] `MainWindow` 注入 `II18nService`（通过 `Program.Services` 解析）
- [x] 提取 `ApplyTranslations()` 方法：刷新标题 / watermark / tooltip / 列头 / 状态栏 / 菜单 / 右键菜单 / 导航树 / 控制台 / 错误面板
- [x] 构造函数末尾调用 `ApplyTranslations()`
- [x] 订阅 `LocaleChanged` 事件，触发 `ApplyTranslations()`（Dispatcher.UIThread.Post）
- [x] 工具按钮文字与 tooltip 分离存储（emoji 部分不翻译，文字部分翻译）
- [x] 菜单项 Header 保持助记符 `_` 前缀（中文菜单无需助记符，去掉 `_`）
- [x] `BindMenuCommands` / `BindContextMenuCommands` 中 header 匹配改为 key 匹配（避免翻译后匹配失败）
- [x] 导航树节点 label 改用 i18n key
- [x] 测试：`I18nComplianceTests.MainWindow_Applies_Chinese_Translations` 通过

### T-306 MainViewModel 对话框消息接入 i18n（D-305）
- [x] `MainViewModel` 构造函数注入 `II18nService`
- [x] Copy / Move 文件夹选择器标题改用 `Translate`
- [x] Delete 确认消息改用 `Translate`
- [x] Open 失败消息改用 `Translate`
- [x] Rename 对话框标题 / 标签 / 校验消息改用 `Translate`
- [x] About 对话框改用 `Translate`
- [x] Properties 对话框 + 属性标签改用 `Translate`
- [x] 控制台 (ok) / (error) 标记改用 `Translate`
- [x] 测试：`I18nComplianceTests.MainViewModel_Dialogs_Translate_To_Chinese` 通过

### T-307 StatusbarViewModel TasksLabel 接入 i18n（D-306）
- [x] `StatusbarViewModel` 构造函数注入 `II18nService`
- [x] `TasksLabel` 改用 `Translate("gui.status.tasks", _activeTaskCount)`
- [x] 订阅 `LocaleChanged` 重新 `RaisePropertyChanged(nameof(TasksLabel))`
- [x] 测试：`I18nComplianceTests.Statusbar_TasksLabel_Translates` 通过

### T-308 MessageBoxWindow 按钮标签接入 i18n（D-307）
- [x] `MessageBoxWindow` 构造函数注入 `II18nService`
- [x] `MapButtons` 返回的按钮 label 改用 `Translate`
- [x] `"Details"` 折叠区标题改用 `Translate`
- [x] 测试：`I18nComplianceTests.MessageBox_Buttons_Translate` 通过

### T-309 InputDialogWindow 按钮标签接入 i18n（D-308）
- [x] `InputDialogWindow` 构造函数注入 `II18nService`
- [x] OK / Cancel 按钮改用 `Translate`
- [x] 测试：`I18nComplianceTests.InputDialog_Buttons_Translate` 通过

### T-310 AvaloniaDialogService 默认标题接入 i18n（D-309）
- [x] `AvaloniaDialogService` 构造函数注入 `II18nService`
- [x] Open / Save As / Select Folder 默认标题改用 `Translate`
- [x] 测试：`I18nComplianceTests` 中 `gui.dialog.openTitle` / `saveAsTitle` / `selectFolderTitle` key 翻译验证通过

### T-311 ItemTypeConverter 接入 i18n（D-310）
- [x] 新增 `I18nAccessor` 静态类（`src/OpenShell.Core/I18n/I18nAccessor.cs`），持有 `II18nService? Instance`
- [x] `ItemTypeConverter.Convert` 改用 `I18nAccessor.Instance?.Translate(...)` 翻译 "File folder" / "File"
- [x] GUI `App.cs` 启动时设置 `I18nAccessor.Instance`
- [x] 测试：`I18nComplianceTests` 中 `gui.type.folder` / `gui.type.file` key 翻译验证通过

### T-312 GlobalSearchWindow + ViewModel 接入 i18n（D-311）
- [x] `GlobalSearchWindow` 注入 `II18nService`，翻译标题 + watermark
- [x] `GlobalSearchViewModel` 注入 `II18nService`，翻译状态文本
- [x] 订阅 `LocaleChanged` 刷新窗口标题 + watermark
- [x] 测试：i18n key 翻译验证通过（GlobalSearch 使用的 key 在 `All_EnUs_Keys_Have_ZhCN_Translation` 中覆盖）

### T-313 QuickLookWindow 接入 i18n（D-312）
- [x] `QuickLookWindow` 注入 `II18nService`，翻译标题 + 预览占位文本
- [x] 订阅 `LocaleChanged` 刷新标题
- [x] 测试：i18n key 翻译验证通过（QuickLook 使用的 key 在 `All_EnUs_Keys_Have_ZhCN_Translation` 中覆盖）

### T-314 ProgressDialogViewModel 接入 i18n（D-313）
- [x] `ProgressDialogViewModel` 构造函数注入 `II18nService`
- [x] `ComputeResultMessage` 改用 `Translate`
- [x] 测试：i18n key 翻译验证通过（ProgressDialog 使用的 key 在 `All_EnUs_Keys_Have_ZhCN_Translation` 中覆盖）

---

## 阶段四：验证

### T-315 全量构建 + 测试验证
- [x] `dotnet build OpenShell.slnx` 0 警告 0 错误
- [x] 全解决方案测试全绿（1986 通过 / 7 跳过 / 0 失败；I18nComplianceTests 14 通过 / 0 跳过 / 0 失败）
- [x] 任务清单全部 `[x]`
- [x] 审计文档状态更新为「已修复」
