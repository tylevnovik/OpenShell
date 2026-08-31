# 最新项目可用性审计

**审计日期**：2026-08-29  
**审计基线**：f590ac3be38991e5e41d1f75bf438530a78c0b01（main）  
**范围**：CLI 参数绑定与输出、GUI 文件操作主链路、导航/预览/拖拽/快捷键、现有测试的真实覆盖度。  
**结论（审计时）**：当时项目虽然可以构建并通过既有自动化测试，但不应按“主要功能可用”对外宣称。至少一个 GUI P0 主链路和一个 CLI P0 参数安全问题已由源码与黑盒运行共同确认；多个 GUI 功能停留在 ViewModel/服务存在，尚未连到可操作控件。2026-08-29 已完成本报告 LP-001～LP-010 的修复与回归，当前状态以第六节为准。

## 一、验证基线

| 项目 | 结果 | 证据 |
|---|---|---|
| 工作树 | 审计开始时干净；当前包含本轮实现与审计文档变更 | 初始基线为 `f590ac3be38991e5e41d1f75bf438530a78c0b01`，修复后以 `git status --short` 核对 |
| 构建 | 0 警告 / 0 错误 | dotnet build OpenShell.slnx --no-restore -v:minimal |
| 全量测试 | 2120 通过 / 2 跳过 / 0 失败 | Core 1900、GUI 75、Integration 15、FileSystem 36、Remote 94；2 个真实 SFTP 跳过 |
| CLI 启动 | 正常 | --version、--help、未知宿主参数均返回稳定结果 |
| GUI 启动 | 进程可启动 | 独立临时目录启动 8 秒，日志显示 Avalonia Host/Application Started；未进行真实鼠标交互 |

审计时既有测试的“全绿”不能覆盖下列问题：tests/OpenShell.Gui.Host.Tests/GuiHostComplianceTests.cs 的选中同步测试只确认 SelectedItems 集合存在，并没有真正触发 UI 选择事件；ViewMode、Preview 等测试也主要验证属性/方法/字段存在。CLI 进程测试覆盖了高频文件命令，但没有覆盖必填参数缺失、未知命令参数和一般对象输出契约；这些缺口已由本轮新增行为测试覆盖。

## 二、已确认缺陷

### LP-001 / P0：CLI 缺少必填参数和未知参数校验

**症状**：命令参数缺失时，有的命令抛空引用，有的命令以成功或空操作结束；未知参数会被静默忽略。

**证据**：

- src/OpenShell.Cli.Host/Program.cs:1512-1632 的 ParseArgs 收集 named 参数后只按已知参数绑定，没有对未匹配的 key 或 Mandatory=true 做统一校验；缺失值可直接以 null 进入 record 构造。
- 黑盒 --command "Set-Config"：exit 1，Object reference not set to an instance of an object。
- 黑盒 --command "Set-Config theme"：exit 3，Value cannot be null。
- 黑盒 --command "Get-Date -Bogus"：exit 0，未报未知参数。
- 在隔离临时目录中，New-Item 无参返回 exit 0 并把当前目录作为目标，Copy-Item 无参返回 exit 0 且报告复制 0 项，Move-Item 无参尝试操作当前目录并返回文件占用错误。这种默认目标/成功空操作语义对破坏性命令不安全。

**影响**：用户输入错误不能稳定得到参数错误；脚本/自动化可能误判成功；文件命令可能把当前目录当作隐含目标。

### LP-002 / P0：GUI 文件列表选择没有同步到 ViewModel

**症状**：用户可以看到 ListBox，但通过鼠标选择后，复制、剪切、删除、重命名、移动、属性等命令拿不到选中项，表现为按钮/快捷键“没反应”。

**证据**：

- src/OpenShell.Gui.Host/Views/FileListView.axaml:33-35 只有 SelectionMode="Multiple"，没有 SelectionChanged 处理器，也没有 SelectedItems 同步机制。
- src/OpenShell.Gui.Host/Views/FileListView.axaml.cs:33-56 的右键逻辑只修改 Avalonia ListBox.SelectedItems，没有写入 BrowserTab.Pane.SelectedItems。
- src/OpenShell.Gui.Host/ViewModels/PaneViewModel.cs:44-58 维护了另一份独立的 SelectedItems 集合。
- src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs:647-658 的剪贴板复制明确以 ActivePane.SelectedItems.Count == 0 为短路条件；删除/移动/重命名等操作采用同一状态源。

**影响**：这是 GUI 文件管理器的核心 P0 阻断，不是单个按钮问题。

### LP-003 / P1：CLI Property 输出丢失实际值

**症状**：Get-Date 能执行，但只输出 Get-Date | 1 项, 0 字节，看不到日期；-Format yyyy 和 -Bogus 也呈现同样结果。

**证据**：

- src/OpenShell.Core/Commands/Builtins/GetDateCommand.cs:64-70 产生 DateTime/Value 属性，但没有 Path 属性。
- src/OpenShell.Cli.Host/Program.cs:795-803 对 ItemKind.Property 只渲染 Properties["Path"]，否则回退到 item.Name，完全不显示 Value。

**影响**：不仅是日期命令，任何返回 Property 且不带 Path 的命令都可能出现“成功但无结果”；管道和脚本输出也有数据契约风险。

### LP-004 / P1：预览面板开关没有实际预览控件

**症状**：ViewModel 有 IsPreviewPaneVisible，菜单也能切换该布尔值，但窗口没有预览区域。

**证据**：

- src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs:316-319 的 BuildPreviewPane() 固定返回 null。
- src/OpenShell.Gui.Host/Views/MainWindow.axaml 的工作区只有 Navigation、FileList、Details 三块，没有 Preview 控件。
- src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs:487-493 只切换状态，不创建或显示预览内容。

### LP-005 / P1：地址栏编辑和多项快捷键不可达

**症状**：地址栏编辑框存在，但没有用户入口；全局搜索、QuickLook、撤销/重做等命令有 ViewModel，但快捷键未接入。

**证据**：

- src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs:185-278 当前处理 F5、Ctrl+T/W/A/C/X/V、Delete、F2、Alt+Enter、导航等，但没有 Ctrl+L/Alt+D、Ctrl+F、Ctrl+Shift+F、Space、Ctrl+Z、Ctrl+Y、Shift+Delete。
- 同文件 :291-305 的 EnterAddressBarEditMode/ExitAddressBarEditMode 存在，但没有调用点。
- src/OpenShell.Gui.Host/Views/BreadcrumbBar.axaml:23-25 的地址输入框没有 Enter/Escape 事件或命令绑定。

### LP-006 / P1：拖拽服务实现了，但 GUI 没有注册任何拖拽目标或拖拽源

**症状**：服务层有拖拽解析与命令分发代码，实际文件列表不能完成 OS 拖入/拖出。

**证据**：

- src/OpenShell.Gui.Host/Services/AvaloniaDragDropService.cs:113-123 提供 RegisterDropTarget，但仓库中除该服务自身定义外没有调用点。
- src/OpenShell.Gui.Host/Services/AvaloniaDragDropService.cs:67-84 提供 StartDragFromPointerAsync，当前 View 没有 Pointer 拖拽手势调用。
- FileListView.axaml/.cs 没有 AllowDrop、DragOver、Drop、Pointer 拖拽接线。

### LP-007 / P1：导航树是静态占位，C/D/快速访问/网络节点不能导航

**症状**：导航树显示“快速访问/此电脑/网络”和 C/D 两个文本项，但只有“此电脑”映射到 fs::/；C/D、快速访问、网络点击后没有实际路径。

**证据**：

- src/OpenShell.Gui.Host/Views/NavigationPane.axaml.cs:29-44 静态创建节点；C/D 节点只有标签，没有路径。
- :61-73 的 switch 只处理 tag == "thisPc"，其他节点路径均为 null。

### LP-008 / P1：ViewMode 和菜单项未连接到文件列表；新窗口菜单是死项

**证据**：

- src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs:431-436 有 ViewMode 属性，但 FileListView.axaml:38-43 固定使用 Details 的五列布局，没有按 ViewMode 切换模板。
- src/OpenShell.Gui.Host/Views/MainWindow.axaml:47-51 的四个视图模式菜单项没有 Command/Click。
- src/OpenShell.Gui.Host/Views/MainWindow.axaml:28 的 New Window 菜单项没有 Command/Click。

## 三、已知但不计入本次新发现的问题

README 已明确项目是 0.1.0-alpha，并列出开发占位：Provider 包签名校验 NullSignatureVerifier、SFTP 凭据明文保存、macOS 代码签名检查恒真、WinRM 未实现。这些是发布成熟度风险，应继续保持显式警告，不能按已完成功能验收。

## 四、仓库文档漂移

审计时 docs/gui-host-audit.md 头部声称 T-400~T-450 全部修复，但其正文仍记录“未挂接 UI”的拖拽、选中同步等问题；本轮已在该文件追加当前接线复核。docs/gui-cli-optimization-tasks.md 的 T-630 仍主要记录启动/静态烟测，不能替代真实窗口交互回归；当前已明确其截图人工验收边界。新的修复已通过 LP 任务与行为合规测试绑定，避免再次出现“存在性测试全绿但功能不可用”。

## 五、审计执行事故记录

本次审计早期曾错误地把无参破坏性命令矩阵放在共享仓库工作目录，导致工作树内容和 .git 元数据被清除。初始 git status 是干净的，仓库对象仍在，随后已从本次基线 commit 重建工作树；恢复后源文件与基线一致，构建和测试均重新验证通过。仓库原有的远端/分支跟踪元数据未保留在本地重建的 .git 配置中；这不是项目功能缺陷，但应在后续需要推送前重新确认 remote 配置。事故后的所有命令复验均使用独立临时目录；当前未提交实现变更均为本轮修复。

## 六、修复后验证（2026-08-29）

本轮按 LP-001～LP-010 完成修复，并以真实进程、真实 Avalonia 控件事件和全量测试复验：

| 项目 | 结果 | 证据 |
|---|---|---|
| 构建 | 0 警告 / 0 错误 | `dotnet build OpenShell.slnx --no-restore -v:minimal` |
| 全量测试 | 2131 通过 / 2 跳过 / 0 失败 | Core 1905、GUI 81、Integration 15、FileSystem 36、Remote 94；2 个跳过仍仅为真实 SFTP 基础设施 |
| 新增 CLI 行为测试 | 5/5 通过 | `LatestProjectComplianceTests`：必填参数、未知参数、Property 实值、AST 参数错误 |
| 新增 GUI 行为测试 | 6/6 通过 | `LatestProjectComplianceTests`：列表选择、预览渲染、地址栏快捷键、拖放注册、导航路径、视图菜单 |
| CLI 黑盒矩阵 | 通过 | `Get-Date -Format yyyy` 输出当前年份；`Get-Date -Bogus`、`Set-Config`、`New-Item`、`Copy-Item`、`Move-Item` 缺参/错参均返回 InvalidArgument（exit 3） |

修复内容包括：统一 CLI/管道/GUI 参数绑定与 AST 参数校验；Property 输出优先显示 Value；文件列表选择双向同步；PreviewPane 文本/代码/图片/归档/PDF/视频最小预览；地址栏、全局搜索、QuickLook、撤销/重做、Shift+Delete 快捷键；拖拽源/目标注册；Favorites、Recent、IDriveRegistry 与真实 Provider 导航；ViewMode 和 New Window 菜单接线。全局搜索和预览的异步 UI 更新也已约束在 Avalonia UI 线程。

当前验证边界：自动化环境没有独立真实桌面窗口截图/鼠标驱动能力，因此未把 1200x800 / 800x500 截图声明为已完成；GUI headless 控件和事件链已通过，真实桌面视觉验收仍需在目标桌面手工执行。
