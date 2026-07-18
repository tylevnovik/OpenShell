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
| T-613 | P1 | D-606 | 将 `SessionTabsService` 接入 GUI 启动、导航和关闭流程 | `[ ]` | T-612 | `Tabs_RestoreAndPersistThroughSessionService` |
| T-614 | P2 | D-607 | 补齐焦点、可访问名称、最小尺寸和中英文复验 | `[ ]` | T-611/T-612 | `InteractiveControls_HaveAccessibleNamesAndFocusStyles` |
| T-615 | P0 | D-608 | 修复文件列表右键菜单到主窗口命令的绑定 | `[ ]` | T-612 | `FileContextMenu_BindsToMainCommands` |
| T-620 | P0 | D-620/D-621/D-625 | 新增早期 CLI 参数解析、help/version 与 usage | `[ ]` | T-600 | `HelpAndVersion_AreSideEffectFree` / `InvalidInvocation_ReturnsUsageError` |
| T-621 | P1 | D-622/D-624 | 清理 CLI 启动日志、统一 UTF-8 与 stdout/stderr | `[ ]` | T-620 | `NonInteractiveOutput_UsesCleanStreams` |
| T-622 | P1 | D-623 | 非交互执行按错误类别和取消语义返回退出码 | `[ ]` | T-620 | `CommandFailure_UsesMappedExitCode` |
| T-630 | P0 | - | 全量构建、测试、CLI 进程烟测和 GUI 双尺寸截图复验 | `[ ]` | T-610~T-622 | 全解决方案 |

## 变更日志

- 2026-07-18 T-600 完成：完成源码、ADR、真实 CLI 与真实 GUI 审计；设计文档已提交；GUI 合规基线 1 通过 / 8 跳过，CLI 合规基线 1 通过 / 5 跳过，0 失败。
- 2026-07-18 T-610 完成：应用显式合并语义颜色、控件与图标资源；Light/Dark token 覆盖表面、文字、边框、状态与焦点；详情面板隐藏时自适应列宽归零。GUI 全套 66 通过 / 6 跳过 / 0 失败。
- 2026-07-18 T-611 完成：导航与文件命令统一为紧凑矢量图标按钮；新建文件夹保留图标加文本；标签增加唯一活动态、新建入口和矢量关闭按钮。GUI 全套 68 通过 / 4 跳过 / 0 失败。
- 2026-07-18 T-612 完成：文件区区分加载、空目录、过滤无结果与可重试错误，并在刷新失败时保留已有列表；状态栏显示选中大小并按需显示任务/错误；详情面板完成 i18n 和空选择处理。GUI 全套 70 通过 / 2 跳过 / 0 失败。实施中新增 D-608/T-615。
