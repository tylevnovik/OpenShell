# GUI and CLI Optimization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 将 OpenShell GUI 和 CLI 提升为视觉一致、交互完整、输出契约稳定且可机械验证的产品化宿主。

**Architecture:** GUI 保持 Avalonia + ReactiveUI 结构，通过语义资源和状态驱动 XAML 修复布局与交互；CLI 在 Host 创建前增加纯参数解析层，现有 `CliHost` 继续负责 REPL 和命令执行。所有行为先由独立合规测试定义，再做最小范围实现。

**Tech Stack:** .NET 8、C#、Avalonia、ReactiveUI、xUnit、FluentAssertions、Microsoft Generic Host。

---

### Task 1: 建立合规基线

**Files:**
- Create: `docs/gui-cli-optimization-audit.md`
- Create: `docs/gui-cli-optimization-tasks.md`
- Create: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`
- Create: `tests/OpenShell.Core.Tests/CliE2E/GuiCliOptimizationCliComplianceTests.cs`

**Steps:**
1. 为 T-610~T-622 写 `Skip="pending T-XXX"` 的行为测试。
2. 保留 GUI 基础工作流和 CLI `-Command` 干净输出两个已实现测试为启用状态。
3. 运行两个测试项目，确认已实现项通过、待实现项仅跳过。
4. 将 T-600 标记完成并提交基线。

### Task 2: 接入 GUI 语义视觉系统

**Files:**
- Modify: `src/OpenShell.Gui.Host/App.axaml`
- Modify: `src/OpenShell.Gui.Host/Styles/Colors.axaml`
- Modify: `src/OpenShell.Gui.Host/Styles/Controls.axaml`
- Modify: `src/OpenShell.Gui.Host/Styles/Icons.axaml`
- Test: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`

**Steps:**
1. 启用 T-610 资源测试并确认失败。
2. 定义 Light/Dark 共用的表面、文字、边框、焦点、状态和强调 token。
3. 在 `App.axaml` 显式合并颜色、控件和图标资源。
4. 移除 View 中影响主题一致性的硬编码颜色。
5. 运行 GUI 合规测试并提交 T-610 的资源部分。

### Task 3: 修复主工作区布局

**Files:**
- Modify: `src/OpenShell.Gui.Host/Views/MainWindow.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs`
- Test: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`

**Steps:**
1. 启用详情栏折叠测试并确认固定列宽导致失败。
2. 为工作区列和详情面板增加稳定名称与自适应宽度。
3. 让详情栏隐藏时列宽归零、显示时恢复合理宽度。
4. 在 1200x800 和 800x500 headless 布局下验证文件区无溢出。
5. 运行 GUI 测试并提交 T-610。

### Task 4: 优化命令栏和标签栏

**Files:**
- Modify: `src/OpenShell.Gui.Host/Views/ToolBar.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/ToolBar.axaml.cs`
- Modify: `src/OpenShell.Gui.Host/Views/MainWindow.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs`
- Modify: `src/OpenShell.Gui.Host/Styles/Icons.axaml`
- Test: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`

**Steps:**
1. 启用 T-611 两项测试并确认字符串图标和缺失活动态失败。
2. 将导航、剪贴板、重命名、删除改为 32px 矢量图标按钮并保留工具提示。
3. 新建命令保留图标加文字，确保 800px 宽度不溢出。
4. 为标签增加活动态、矢量关闭按钮和新建标签按钮。
5. 验证鼠标与 `Ctrl+T/W` 行为，运行 GUI 测试并提交 T-611。

### Task 5: 完成文件区、状态栏和详情面板

**Files:**
- Modify: `src/OpenShell.Gui.Host/Views/FileListView.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/FileListView.axaml.cs`
- Modify: `src/OpenShell.Gui.Host/Views/StatusBar.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/DetailsPane.axaml`
- Modify: `src/OpenShell.Gui.Host/ViewModels/PaneViewModel.cs`
- Modify: `src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs`
- Modify: `src/OpenShell.Core/Resources/i18n/en-US.json`
- Modify: `src/OpenShell.Core/Resources/i18n/zh-CN.json`
- Test: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`

**Steps:**
1. 启用 T-612 测试并确认空状态、选中大小和详情本地化失败。
2. 增加空目录、过滤无结果、加载和可重试错误状态。
3. 统一表头和行的共享列宽，窄窗口时允许次要列收缩。
4. 显示选中大小，降低无任务/无错误入口的视觉权重。
5. 详情面板改用 i18n 字段和无选择状态，运行中英文 GUI 测试并提交 T-612。

### Task 6: 接入标签会话持久化

**Files:**
- Modify: `src/OpenShell.Gui.Host/App.cs`
- Modify: `src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs`
- Modify: `src/OpenShell.Gui.Host/Services/SessionTabsService.cs`
- Modify: `tests/OpenShell.Gui.Host.Tests/TestAppBuilder.cs`
- Test: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`

**Steps:**
1. 启用 T-613 测试并确认服务未被消费。
2. GUI 启动时加载默认会话并在 ViewModel 建立后恢复标签。
3. 对新增、关闭、切换和路径变化生成防抖快照。
4. 关闭窗口前刷新最新快照并释放会话锁。
5. 运行会话与 GUI 测试并提交 T-613。

### Task 7: 完成 GUI 可访问性

**Files:**
- Modify: `src/OpenShell.Gui.Host/Styles/Controls.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/MainWindow.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/ToolBar.axaml`
- Modify: `src/OpenShell.Gui.Host/Views/FileListView.axaml`
- Test: `tests/OpenShell.Gui.Host.Tests/GuiCliOptimizationGuiComplianceTests.cs`

**Steps:**
1. 启用 T-614 测试并确认缺少可访问名称或焦点样式。
2. 为所有图标按钮设置 AutomationProperties.Name 和工具提示。
3. 为键盘焦点定义明暗主题均可见的 2px 焦点环。
4. 验证 Tab 顺序、快捷键和最小窗口尺寸。
5. 运行 GUI 全套测试并提交 T-614。

### Task 8: 建立 CLI 早期调用模型

**Files:**
- Create: `src/OpenShell.Cli.Host/CliInvocationOptions.cs`
- Create: `src/OpenShell.Cli.Host/CliUsage.cs`
- Modify: `src/OpenShell.Cli.Host/Program.cs`
- Test: `tests/OpenShell.Core.Tests/CliE2E/GuiCliOptimizationCliComplianceTests.cs`

**Steps:**
1. 启用 T-620 help/version/错误调用测试并确认失败。
2. 实现无 I/O 的参数解析结果：运行模式、选项、错误和退出码。
3. 在 Host 创建前处理 help、version、未知参数、缺失值和互斥模式。
4. 保持现有参数别名与 `-ExecutionPolicy` 兼容。
5. 运行 CLI 进程测试并提交 T-620。

### Task 9: 清理 CLI 输出并修正退出码

**Files:**
- Modify: `src/OpenShell.Cli.Host/Program.cs`
- Modify: `src/OpenShell.Cli.Host/CliUsage.cs`
- Modify: `tests/OpenShell.Core.Tests/CliE2E/CliProcessRunner.cs`
- Test: `tests/OpenShell.Core.Tests/CliE2E/GuiCliOptimizationCliComplianceTests.cs`

**Steps:**
1. 启用 T-621/T-622 测试并确认日志污染或退出码不匹配。
2. 设置 UTF-8 输入输出，并将默认控制台日志阈值提高到 Warning。
3. help/version 仅写 stdout，调用错误仅写 stderr。
4. `RunCommandAsync` 根据最新错误调用 `ExitCodes.For`，取消返回 `Cancelled`。
5. 运行全部 CLI E2E 测试并提交 T-621/T-622。

### Task 10: 全量验证与真实窗口复验

**Files:**
- Modify: `docs/gui-cli-optimization-audit.md`
- Modify: `docs/gui-cli-optimization-tasks.md`
- Modify: `docs/project-stability-tasks.md`

**Steps:**
1. 运行 `dotnet build OpenShell.slnx --nologo`，预期 0 警告 / 0 错误。
2. 用 TRX 运行全解决方案测试，预期 0 失败且仅两个 SFTP Skip。
3. 真实进程验证 help、version、无效参数、`-Command` 和 `-File` 的输出与退出码。
4. 真实窗口验证 Dark/Light 的 1200x800 和 800x500 截图，无空列、溢出、资源键或低对比状态栏。
5. 将 T-600~T-630 和旧 T-591 全部标记完成并提交最终验证记录。

