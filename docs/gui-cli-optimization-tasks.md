# GUI 与 CLI 产品化优化任务清单

- **创建日期**: 2026-07-18
- **关联审计**: `docs/gui-cli-optimization-audit.md`
- **关联设计**: `docs/plans/2026-07-18-gui-cli-optimization-design.md`
- **合规测试**: `GuiCliOptimizationGuiComplianceTests.cs` / `GuiCliOptimizationCliComplianceTests.cs`

状态标记：`[ ]` 待办 / `[~]` 进行中 / `[x]` 完成 / `[!]` 阻塞

| ID | 优先级 | 缺陷 | 任务 | 状态 | 依赖 | 对应测试 |
|----|--------|------|------|------|------|----------|
| T-600 | P0 | - | 建立设计、审计、任务清单、实施计划和合规测试基线 | `[x]` | - | 新合规套件 |
| T-610 | P0 | D-600/D-601 | 接入语义主题资源并修复隐藏详情栏占宽 | `[x]` | T-600 | `App_LoadsSemanticDesignResources` / `HiddenDetailsPane_CollapsesWorkspaceColumn` |
| T-611 | P1 | D-603 | 重做紧凑命令栏与具有活动态的新标签栏 | `[x]` | T-610 | `Toolbar_UsesAccessibleVectorCommands` / `TabStrip_ExposesActiveAndNewTabStates` |
| T-612 | P1 | D-602/D-604/D-605 | 完成文件状态、响应式列、状态栏和详情面板 | `[x]` | T-610 | `FileWorkspace_ExposesCompleteStates` / `StatusAndDetails_AreCompleteAndLocalized` |
| T-613 | P1 | D-606 | 将 `SessionTabsService` 接入 GUI 启动、导航和关闭流程 | `[x]` | T-612 | `Tabs_RestoreAndPersistThroughSessionService` |
| T-614 | P2 | D-607 | 补齐焦点、可访问名称、最小尺寸和中英文复验 | `[x]` | T-611/T-612 | `InteractiveControls_HaveAccessibleNamesAndFocusStyles` |
| T-615 | P0 | D-608 | 修复文件列表右键菜单到主窗口命令的绑定 | `[x]` | T-612 | `FileContextMenu_BindsToMainCommands` |
| T-616 | P0 | D-609 | 让活动标签切换通知所有 `ActivePane` 绑定 | `[x]` | T-613 | `ActiveTabSwitch_NotifiesActivePaneBindings` |
| T-620 | P0 | D-620/D-621/D-625 | 新增早期 CLI 参数解析、help/version 与 usage | `[x]` | T-600 | `HelpAndVersion_AreSideEffectFree` / `InvalidInvocation_ReturnsUsageError` |
| T-621 | P1 | D-622/D-624 | 清理 CLI 启动日志、统一 UTF-8 与 stdout/stderr | `[x]` | T-620 | `NonInteractiveOutput_UsesCleanStreams` |
| T-622 | P1 | D-623 | 非交互执行按错误类别和取消语义返回退出码 | `[x]` | T-620 | `CommandFailure_UsesMappedExitCode` |
| T-630 | P0 | - | 全量构建、测试、CLI 进程烟测和 GUI 双尺寸截图复验 | `[ ]` | T-610~T-622 | 全解决方案 |

## 变更日志

- 2026-07-18 T-600 完成：完成源码、ADR、真实 CLI 与真实 GUI 审计；设计文档已提交；GUI 合规基线 1 通过 / 8 跳过，CLI 合规基线 1 通过 / 5 跳过，0 失败。
- 2026-07-18 T-610 完成：应用显式合并语义颜色、控件与图标资源；Light/Dark token 覆盖表面、文字、边框、状态与焦点；详情面板隐藏时自适应列宽归零。GUI 全套 66 通过 / 6 跳过 / 0 失败。
- 2026-07-18 T-611 完成：导航与文件命令统一为紧凑矢量图标按钮；新建文件夹保留图标加文本；标签增加唯一活动态、新建入口和矢量关闭按钮。GUI 全套 68 通过 / 4 跳过 / 0 失败。
- 2026-07-18 T-612 完成：文件区区分加载、空目录、过滤无结果与可重试错误，并在刷新失败时保留已有列表；状态栏显示选中大小并按需显示任务/错误；详情面板完成 i18n 和空选择处理。GUI 全套 70 通过 / 2 跳过 / 0 失败。实施中新增 D-608/T-615。
- 2026-07-18 T-615 完成：`BrowserTab` 暴露只读 Owner，文件列表右键菜单的窗口级命令全部改为 `Owner.*Command`，不再绑定到错误的标签 DataContext。GUI 全套 72 通过 / 2 跳过 / 0 失败。
- 2026-07-18 T-613 实施中发现 D-609：`ActivePane` 无属性通知，新增 T-616 行为测试。
- 2026-07-18 T-613 完成：GUI 启动加载并锁定命名会话，恢复标签路径/排序/活动索引；标签变化防抖保存，应用退出显式 `FlushAsync` 后释放锁；修复 SessionTabsService dispose 保存竞态。GUI 全套 73 通过 / 2 跳过 / 0 失败。
- 2026-07-18 T-616 完成：`ActivePane` 改为带 `RaiseAndSetIfChanged` 的响应式属性，标签切换会刷新面包屑、详情、状态和相关命令绑定。GUI 全套 74 通过 / 1 跳过 / 0 失败。
- 2026-07-18 T-614 完成：按钮、文本框、列表项、树项和分隔拖柄增加高对比焦点态；图标命令提供自动化名称，ControlLocalizer 同步翻译屏幕阅读器名称。GUI 全套 75 通过 / 0 跳过 / 0 失败。
- 2026-07-18 T-620 完成：新增无副作用早期参数解析器和稳定 usage/version 渲染；help/version、未知参数、缺失值、互斥模式和非法执行策略在 Host 创建前返回。CLI E2E 30 通过 / 1 跳过 / 0 失败。
- 2026-07-18 T-621 完成：进程最早期固定无 BOM UTF-8；Console provider 默认只显示 Warning 以上，结构化 provider 保留完整日志；插件成功提示仅在交互模式显示。CLI E2E 32 通过 / 1 跳过 / 0 失败。
- 2026-07-18 T-622 完成：命令与脚本按本次新增错误调用 `ExitCodes.For`；脚本解析返回 2、命令缺失返回 4、取消返回 7；REPL 自动变量不再受历史错误污染。CLI E2E 34 通过 / 0 跳过 / 0 失败。
