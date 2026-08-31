# 最新项目可用性修复实施计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复 LP-001～LP-010，使 CLI 错误输入安全可诊断、GUI 文件管理主链路可操作，并让测试验证真实行为。

**Architecture:** 保留现有 Core command/provider/preview/drag-drop 服务；新增共享 CLI 参数绑定器；GUI 由 FileListView 同步唯一选择集合，MainWindow 接管窗口级快捷键/菜单，PreviewPane 复用 IPreviewService。

**Tech Stack:** .NET 8、C#、Avalonia 11、ReactiveUI、xUnit、Avalonia.Headless、FluentAssertions。

---

### Task 1: 建立合规测试基线

**Files:**
- Create: tests/OpenShell.Core.Tests/LatestProjectComplianceTests.cs
- Create: tests/OpenShell.Gui.Host.Tests/LatestProjectComplianceTests.cs
- Modify: docs/latest-project-tasks.md

**Steps:**

1. 为 LP-001～LP-008 建立带 pending LP-* 的跳过测试。
2. 运行两个新增测试文件，确认测试可发现且只产生预期 Skip。
3. 将任务清单状态改为进行中。

### Task 2: 修复共享 CLI 参数绑定

**Files:**
- Create: src/OpenShell.Core/Commands/CommandArgumentBinder.cs
- Modify: src/OpenShell.Cli.Host/Program.cs
- Modify: src/OpenShell.Core/Pipeline/PipelineExecutor.cs
- Test: tests/OpenShell.Core.Tests/LatestProjectComplianceTests.cs

**Steps:**

1. 先解除 LP-001 对应 Skip，运行失败测试。
2. 实现参数名/别名索引、未知参数、重复参数、必填参数和缺失值校验。
3. 将类型转换异常包装为 InvalidArgument 语义，保证普通命令和 Pipeline 共用。
4. 明确破坏性文件命令缺少路径时只能报错，不能隐式解析当前目录。
5. 运行 CLI 进程级测试和新合规测试。

### Task 3: 修复 CLI Property 输出

**Files:**
- Modify: src/OpenShell.Cli.Host/Program.cs
- Test: tests/OpenShell.Core.Tests/LatestProjectComplianceTests.cs

**Steps:**

1. 解除 LP-003 Skip，确认 Get-Date 当前失败。
2. 设计 Property 的 Value/Path/Name 回退顺序，保持 Location 等路径对象可读。
3. 添加 Get-Date、Convert-* 和管道输出断言。
4. 运行 Core、CLI E2E 与隔离目录黑盒矩阵。

### Task 4: 修复 GUI 选择状态和文件操作

**Files:**
- Modify: src/OpenShell.Gui.Host/Views/FileListView.axaml
- Modify: src/OpenShell.Gui.Host/Views/FileListView.axaml.cs
- Modify: src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs
- Test: tests/OpenShell.Gui.Host.Tests/LatestProjectComplianceTests.cs

**Steps:**

1. 解除 LP-002 Skip，验证真实 ListBox 选择不能更新 Pane。
2. 添加 SelectionChanged 同步，处理普通选择、Ctrl/Shift 多选、右键选择、刷新和换 tab。
3. 保证 UI 重建期间不会递归清空或覆盖选择。
4. 通过真实 ViewModel 命令验证复制/删除等命令收到选中项。

### Task 5: 接通预览面板

**Files:**
- Create: src/OpenShell.Gui.Host/Views/PreviewPane.axaml
- Create: src/OpenShell.Gui.Host/Views/PreviewPane.axaml.cs
- Modify: src/OpenShell.Gui.Host/Views/MainWindow.axaml
- Modify: src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs
- Modify: src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs
- Test: tests/OpenShell.Gui.Host.Tests/LatestProjectComplianceTests.cs

**Steps:**

1. 解除 LP-004 Skip，验证 PreviewPane 当前不存在。
2. 接入选中项变化、IPreviewService 和取消/错误状态。
3. 支持文本/代码、图片、归档和不支持类型的最小嵌入渲染。
4. 让 View > Preview Pane 真正显示/隐藏控件，验证 1200x800 与 800x500 布局约束。

### Task 6: 接通快捷键、地址栏和菜单

**Files:**
- Modify: src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs
- Modify: src/OpenShell.Gui.Host/Views/BreadcrumbBar.axaml
- Modify: src/OpenShell.Gui.Host/Views/MainWindow.axaml
- Modify: src/OpenShell.Gui.Host/Views/ToolBar.axaml
- Test: tests/OpenShell.Gui.Host.Tests/LatestProjectComplianceTests.cs

**Steps:**

1. 解除 LP-005 与 LP-008 Skip，验证当前按键/菜单无效果。
2. 接入 Ctrl+L/Alt+D、Enter/Escape、Ctrl+F、Ctrl+Shift+F、Space、Ctrl+Z/Y、Shift+Delete。
3. 给地址栏编辑框绑定提交/取消，并处理 TextBox 焦点。
4. 接通 ViewMode 菜单、新建文件、撤销/重做、新窗口菜单。
5. 用 Headless 事件测试命令调用和状态变化。

### Task 7: 接通拖拽和导航

**Files:**
- Modify: src/OpenShell.Gui.Host/Views/FileListView.axaml
- Modify: src/OpenShell.Gui.Host/Views/FileListView.axaml.cs
- Modify: src/OpenShell.Gui.Host/Views/NavigationPane.axaml.cs
- Modify: src/OpenShell.Gui.Host/Views/NavigationPane.axaml
- Modify: src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs
- Test: tests/OpenShell.Gui.Host.Tests/LatestProjectComplianceTests.cs

**Steps:**

1. 解除 LP-006/LP-007 Skip，确认当前服务没有 View 调用点。
2. 在文件列表注册 DropTarget，接入 Pointer 拖出、DragOver、Drop 和选择状态。
3. 从 Provider/系统盘、Favorites、Recent 和 mounted drives 创建带真实 ItemPath 的节点。
4. 所有节点点击都调用 NavigateCommand，空/失效路径显示错误而不吞掉。
5. 添加拖入/拖出和导航节点行为测试。

### Task 8: 收紧测试与文档

**Files:**
- Modify: tests/OpenShell.Gui.Host.Tests/GuiHostComplianceTests.cs
- Modify: tests/OpenShell.Core.Tests/CliE2E/CliProcessE2EComplianceTests.cs
- Modify: docs/latest-project-audit.md
- Modify: docs/latest-project-tasks.md
- Modify: docs/gui-host-audit.md
- Modify: docs/gui-cli-optimization-audit.md

**Steps:**

1. 删除已被新行为测试覆盖的假阳性存在性断言，保留有价值的接口/资源断言。
2. 将 LP-001～LP-010 状态逐项改为完成并记录验证命令。
3. 更新旧审计中的过时“已完成”与“未接线”结论。
4. 运行 dotnet build OpenShell.slnx、全解决方案测试、CLI 真实进程矩阵。
5. 执行 GUI 启动、窗口双尺寸和关键交互人工验收；无法自动化的部分明确记录边界。

