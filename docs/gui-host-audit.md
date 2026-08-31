# GUI Host 审计报告

- **主题**: GUI Host 不符合 Windows Explorer 设计标准 / 视觉显示异常 / 功能运作异常
- **关联 ADR**: ADR-0013（GUI MVVM/ReactiveUI）、ADR-0027（主题/快捷键）、ADR-0028（菜单/工具栏）、ADR-0029（剪贴板/拖拽）、ADR-0030（预览/搜索）、ADR-0034（会话恢复）、ADR-0035（i18n）
- **日期**: 2026-07-11（历史基线）；2026-08-29 当前复核
- **状态**: 历史 T-400~T-450 结论保留作变更记录；本文件正文中的“未接线”描述是历史快照。当前 LP-001~LP-010 的实现状态、自动化证据和验证边界以 `docs/latest-project-audit.md` 第六节为准。

> **2026-08-29 复核说明**：本报告创建于当前 GUI 接线完成前，原有章节用于保留问题来源，不能单独代表当前源码状态。当前文件列表已同步选择并注册拖放，QuickLook/全局搜索/地址栏/PreviewPane/ViewMode 菜单已接线，导航树携带可执行路径；未能在自动化环境完成的真实桌面双尺寸截图仍属于人工验收项。

---

## 1. 现状概览

### 1.1 已有基础设施

| 组件 | 位置 | 状态 |
|------|------|------|
| `MainWindow` | `src/OpenShell.Gui.Host/Views/MainWindow.cs` | 单窗格 Explorer 风格，纯 C# code-behind（无 .axaml） |
| `MainViewModel` | `src/OpenShell.Gui.Host/ViewModels/MainViewModel.cs` | ReactiveViewModel，导航历史栈 + 命令 |
| `PaneViewModel` | `src/OpenShell.Gui.Host/ViewModels/PaneViewModel.cs` | Items / SelectedItems / SortColumn / Refresh |
| `StatusbarViewModel` | `src/OpenShell.Gui.Host/ViewModels/StatusbarViewModel.cs` | ItemCount / SelectedCount / TasksLabel |
| `AvaloniaClipboardService` | `src/OpenShell.Gui.Host/Services/AvaloniaClipboardService.cs` | 4 格式互操作（OpenShellItems/uri-list/plain/FileNames）+ Cut 模式 — **未注册到 DI** |
| `AvaloniaDragDropService` | `src/OpenShell.Gui.Host/Services/AvaloniaDragDropService.cs` | 4 格式 DataObject + 修饰键协商 — **未注册到 DI / 未挂接 UI** |
| `QuickLookWindow` | `src/OpenShell.Gui.Host/Views/QuickLookWindow.cs` | 7 种预览（Text/Code/Image/Archive/PDF/Video/NotSupported）— **未绑定空格键** |
| `GlobalSearchWindow` | `src/OpenShell.Gui.Host/Views/GlobalSearchWindow.cs` | 完整实现 — **未挂接 Ctrl+Shift+F** |
| `SessionTabsService` | `src/OpenShell.Gui.Host/Services/SessionTabsService.cs` | 已注册 — **从未被 App/MainWindow 调用** |
| `IFavoritesService` / `IRecentService` | `AppBuilder.cs:193-194` | 已注册 — **未集成到导航树** |
| `IDriveRegistry` | `AppBuilder.cs:106` | 已注册 — **未用于动态枚举盘符** |
| `IThemeService` | `AppBuilder.cs:189` | 已注册 — **App/MainWindow 从未使用** |
| `IUndoService` | `AppBuilder.cs:123` | 已注册 — **MainViewModel 未注入** |
| `IConfigurationService` | `AppBuilder.cs:198` | 已注册 — **未用于窗口/列宽/排序持久化** |
| i18n 集成 | 全代码 | 完整（T-305~T-315 已完成） |

### 1.2 核心结论

GUI Host 代码组织清晰、i18n 完整、ReactiveUI 异常订阅规范，但存在三大类系统性问题：

1. **大量已实现服务与 UI 脱钩**：剪贴板、拖拽、QuickLook、全局搜索、标签页、收藏夹、最近文件、盘符枚举、主题、撤销等服务均已注册到 DI，但 `MainWindow` 从未调用它们，导致功能完全不可用。
2. **选中状态未双向绑定**（F-10）：`ListBox.SelectedItems` 与 `PaneViewModel.SelectedItems` 不同步，导致所有文件操作命令（Copy/Move/Delete/Rename）因 `SelectedItems.Count == 0` 直接 return，**用户在 UI 中选中文件后点任何操作按钮都无反应**。
3. **快捷键体系严重残缺**：缺 Ctrl+C/X/V、空格预览、Ctrl+F 搜索、Ctrl+Z 撤销、Alt+Enter 属性、Shift+Delete 永久删除等 Explorer 标准快捷键；且 `OnGlobalKeyDown` 未排除 TextBox 焦点，在搜索框/控制台输入时按 Delete/F2/Ctrl+A 会误触发文件操作。

**问题统计**：Critical 18 / High 27 / Medium 22 / Low 11，合计 78 项。

### 1.3 当前接线复核（2026-08-29）

| 功能 | 当前状态 | 自动化边界 |
|------|------|------|
| 文件列表选择 | `SelectionChanged` 与 `Pane.SelectedItems` 双向同步 | Avalonia headless 真实选择通过 |
| 剪贴板/拖放 | DI 已注册；文件列表注册 drop target，Pointer 手势调用 drag source | OS 原生拖动效果需桌面手工验收 |
| QuickLook/Preview | 空格调用 QuickLook；PreviewPane 订阅活动 Pane，支持文本/代码/图片/归档等 | 真实窗口尺寸与渲染仍需手工截图 |
| 地址栏/搜索/导航 | Ctrl+L/Alt+D、Enter/Escape、全局搜索入口及带路径导航节点已接线 | 全局搜索依赖真实索引/Provider 数据 |
| ViewMode/新窗口 | 四种模板由 ViewMode 驱动；菜单点击切换；New Window 创建共享会话状态的次窗口 | 次窗口独立会话语义仍是未来增强 |

---

## 2. 功能运作问题（Functional）

### F-01 多标签页完全未实现【Critical】
- **位置**: `MainWindow.cs:184-187`；`AppBuilder.cs:202`
- **证据**: `MainWindow.Content` 是 `DockPanel`，仅含单个 `_fileList`，无任何 `TabControl`/`TabStrip`。`SessionTabsService` 已注册但从未调用 `LoadTabsFromSessionAsync`。
- **期望**（ADR-0013 §1）: 顶部 Tab 栏，新建/关闭/切换/拖出独立窗口，关闭 GUI 时持久化，重开恢复。

### F-02 「新建文件夹」按钮无任何事件处理【Critical】
- **位置**: `MainWindow.cs:49`、`MainWindow.cs:190-197`
- **证据**: `_newFolderButton` 字段已声明并加入工具栏（L408），但 L190-197 只挂接了 8 个按钮的 Click，**没有 `_newFolderButton.Click`**。`MainViewModel` 也没有 `NewFolderCommand`。
- **期望**: Explorer 工具栏「New」按钮 + Ctrl+Shift+N 创建新文件夹。

### F-03 无「新建文件」功能【High】
- **位置**: `MainWindow.cs:49-53`；`MainViewModel.cs:218-234`
- **证据**: 工具栏与命令列表均无 `NewFileCommand`。

### F-04 剪贴板复制/剪切/粘贴快捷键全部缺失【Critical】
- **位置**: `MainWindow.cs:880-907`（`OnGlobalKeyDown`）
- **证据**: 仅处理 F5/Alt+Up/Alt+Left/Alt+Right/F2/Delete/Ctrl+A/Ctrl+\`。**完全没有** Ctrl+C、Ctrl+X、Ctrl+V。`CopyCommand` 实际是「弹出文件夹选择对话框，把选中项复制到目标目录」，不是剪贴板语义。
- **期望**: Ctrl+C 复制路径到剪贴板、Ctrl+X 剪切、Ctrl+V 粘贴文件到当前目录。

### F-05 `IClipboardService`/`IDragDropService` 未注册到 DI【Critical】
- **位置**: `AppBuilder.cs:88-254`
- **证据**: `AvaloniaClipboardService` 与 `AvaloniaDragDropService` 类已实现，但 `ConfigureServices` 中**没有任何一行**注册它们。整个剪贴板/拖拽子系统与 GUI 完全脱钩。
- **修复方向**: 在 `AppBuilder.cs` 注册（注意 `AvaloniaDragDropService` 依赖 `CommandDispatchingDragDropService`，需一并注册）。

### F-06 拖拽（DragDrop）未在 `MainWindow` 挂接【Critical】
- **位置**: `MainWindow.cs`（全文无 `DragDrop.SetAllowDrop` / `PointerPressed` 启动拖拽）
- **证据**: `_fileList` 是普通 `ListBox`，无 `PointerPressed` 处理器调 `StartDragFromPointerAsync`；无 `DragDrop.SetAllowDrop(_fileList, true)`；无 `DragOverEvent`/`DropEvent` 订阅。
- **期望**: Explorer 拖拽：按住项拖动到目录即移动/复制；Ctrl=Copy、Shift=Move、Alt=Link。

### F-07 QuickLook 空格预览未绑定【Critical】
- **位置**: `MainWindow.cs:880-907`（无 `Key.Space`）
- **证据**: `IQuickLookWindow` 已注册（`AppBuilder.cs:207`），`QuickLookWindow` 实现完整，但 `MainWindow` 没有任何代码调用 `QuickLookCommand` 或处理空格键。
- **期望**: 选中文件按空格弹出 Quick Look 预览。

### F-08 搜索框完全无事件、无绑定【Critical】
- **位置**: `MainWindow.cs:37-42`、`MainWindow.cs:255`、`MainWindow.cs:390`
- **证据**: `_searchBox` 仅设置 Watermark 并加入布局。**没有 `KeyDown` 处理器、没有 `TextChanged` 绑定、没有 `Bind` 调用**。输入搜索文本完全无效果。
- **期望**: Explorer 顶部搜索框：输入即触发当前目录过滤；Enter 跳转；Ctrl+F 聚焦。

### F-09 全局搜索窗口（Ctrl+Shift+F）未挂接【High】
- **位置**: `MainWindow.cs:880-907`；`GlobalSearchWindow.cs` 已实现但无调用方
- **证据**: `GlobalSearchWindow` + `GlobalSearchViewModel` 完整实现，但 `MainWindow` 从未创建它或处理 Ctrl+Shift+F。

### F-10 `ListBox.SelectedItems` 未与 `PaneViewModel.SelectedItems` 双向绑定【Critical】
- **位置**: `MainWindow.cs:635`（仅 `_fileList.ItemsSource = _vm.ActivePane?.Items`）
- **证据**: Avalonia 11 的 `ListBox.SelectedItems` 是只读属性，不能直接 `Bind`。`MainWindow` 没有用 `SelectionChanged` 事件同步。结果：
  - 用户在 UI 中选中项，`PaneViewModel.SelectedItems` 不更新
  - `SelectAllCommand` 修改 `ActivePane.SelectedItems`，UI 不反映
  - 状态栏 `SelectedCount` 永远为 0
  - Copy/Move/Delete/Rename 命令因 `SelectedItems.Count == 0` 直接 return
- **修复方向**: 挂接 `_fileList.SelectionChanged`，把 `ListBox.SelectedItems` 同步到 `PaneViewModel.SelectedItems`。

### F-11 地址栏不可编辑【High】
- **位置**: `MainWindow.cs:33-36`、`MainWindow.cs:689-741`
- **证据**: `_breadcrumb` 是 `ItemsControl`，仅显示可点击的 `Button` 段。没有切换到 `TextBox` 编辑模式的逻辑。
- **期望**: 面包屑显示 + 文本编辑双模式切换；Ctrl+L / Alt+D 聚焦并切到编辑模式。

### F-12 无视图模式切换（Details/Icons/Tiles/List）【High】
- **位置**: `MainWindow.cs:64-68`、`MainWindow.cs:354-359`
- **证据**: 文件列表是单一 `ListBox` + `FuncDataTemplate`（5 列 Grid）。View 菜单只有 Refresh/ToggleConsole/ErrorPanel。

### F-13 双窗格未实现（ADR-0013 §1 承诺）【Medium】
- **位置**: `MainViewModel.cs:77-79`、`MainViewModel.cs:162-165`
- **证据**: `RightPane` 已创建但 `ActivePane = LeftPane` 永远固定。`MainWindow` 中无第二个 `ListBox`。
- **决策**: 保留 Explorer 单窗格模式（符合 Win11 Explorer），更新 ADR-0013 标注。

### F-14 命令面板（Ctrl+Shift+P）未实现【High】
- **位置**: `MainWindow.cs:880-907`；ADR-0013 §1/§3
- **证据**: `ICompletionProvider` 已注册（`AppBuilder.cs:158`），但无 `CommandPaletteViewModel`，无 Ctrl+Shift+P 处理。

### F-15 无 Undo/Redo（Ctrl+Z/Ctrl+Y）【High】
- **位置**: `AppBuilder.cs:123`（`IUndoService` 已注册）；`MainWindow.cs:880-907`；`MainViewModel.cs:218-234`
- **证据**: `IUndoService` 在 DI 中，但 `MainViewModel` 没有注入和暴露 Undo/Redo 命令。

### F-16 「新窗口」菜单项是 TODO 占位【Medium】
- **位置**: `MainWindow.cs:834`
- **证据**: `ReactiveCommand.Create(() => { /* TODO: new window */ })` — 点击 File > New Window 无任何效果。

### F-17 右键菜单严重不完整【High】
- **位置**: `MainWindow.cs:448-461`
- **证据**: 仅有 Open/Copy/Move/Delete/Rename/Properties。对比 Explorer 缺失：Open with、Open in new window/tab、Pin to Quick access、Cut（与 Copy 区分）、Paste、Copy as path、Create shortcut、Sort 子菜单、View 子菜单。

### F-18 网络节点无子项、无导航【Medium】
- **位置**: `MainWindow.cs:770`
- **证据**: `var network = new TreeViewItem { Header = T("gui.nav.network") };` — 无 `Items`，无 `Tapped` 处理。

### F-19 「此电脑」节点未动态枚举磁盘【High】
- **位置**: `MainWindow.cs:760-768`
- **证据**: 硬编码 `LocalDisk C:/` + `home`。`IDriveRegistry` 已注册但从未使用。不显示 D:/E:/可移动磁盘/网络驱动器。

### F-20 Quick access 节点未集成 IFavoritesService/IRecentService【Medium】
- **位置**: `MainWindow.cs:748-758`；`AppBuilder.cs:193-194`
- **证据**: `IFavoritesService`/`IRecentService` 已注册，但 `BuildNavTree` 硬编码 Desktop/Downloads/Documents/Pictures。

### F-21 导航树不与当前位置同步【Medium】
- **位置**: `MainWindow.cs:744-776`
- **证据**: `BuildNavTree` 一次性构建，不订阅 `ActivePane.CurrentLocation` 变化。用户通过面包屑/后退前进导航时，导航树不高亮当前路径。

### F-22 `OpenCoreAsync` 失败信息写入隐藏的控制台【Medium】
- **位置**: `MainViewModel.cs:355`、`MainViewModel.cs:365`
- **证据**: 非 fs 路径或打开失败时，`CommandOutput = T("...")`。但 `CommandOutput` 绑定到 `_consoleOutputBox`，而控制台默认 `IsVisible = false`。用户看不到错误。
- **修复方向**: 失败时写入 `_errors` 流（`IErrorStream`），让状态栏错误计数+1。

### F-23 排序命令订阅未释放【Low】
- **位置**: `MainWindow.cs:200-203`
- **证据**: 每次列头点击都 `vm.ActivePane.SortCommand.Execute(col).Subscribe()`，返回的 `IDisposable` 被丢弃。每次点击泄漏一个订阅。
- **修复方向**: 改用 `_vm.ActivePane.SortCommand.Execute(col).Subscribe().DisposeWith(...)` 或缓存到 `CompositeDisposable`。

### F-24 Backspace 不导航到上级【Low】
- **位置**: `MainWindow.cs:880-907`
- **证据**: Explorer 用 Backspace 等价于 Alt+Up。`OnGlobalKeyDown` 未处理 `Key.Back`。

### F-25 无 F1 帮助、Alt+Enter 属性、Shift+Delete 永久删除【Low】
- **位置**: `MainWindow.cs:880-907`

### F-26 复制/移动后选中丢失无提示【Low】
- **位置**: `MainViewModel.cs:308-322`；`PaneViewModel.cs:172-193`

### F-27 工具按钮 emoji 前缀在翻译后丢失【Low】
- **位置**: `MainWindow.cs:266-275` vs `MainWindow.cs:49-53`
- **证据**: 字段初始化器 `Content = "📋 Copy"`，`ApplyTranslations` 设 `Content = T("gui.button.copy")`（无 emoji）。视觉前后不一致。
- **修复方向**: 统一为图标控件 + 文本，不依赖 emoji。

### F-28 Service Locator 反模式（多处）【Medium】
- **位置**: `MainWindow.cs:133`、`MainViewModel.cs:74`、`StatusbarViewModel.cs:32`、`GlobalSearchViewModel.cs:58`、`ProgressDialogViewModel.cs:32`、`MessageBoxWindow.cs:40`、`InputDialogWindow.cs:41`、`QuickLookWindow.cs:62`、`GlobalSearchWindow.cs:39`、`AvaloniaDialogService.cs:36`
- **证据**: 均使用 `Program.Services?.GetService(typeof(II18nService)) as II18nService`。
- **修复方向**: 通过构造函数注入。View 层在 `App.OnFrameworkInitializationCompleted` 时解析并传入。

### F-29 `ReactiveViewModel.Dispose` 未被调用【Low】
- **位置**: `App.cs:34-48`、`ReactiveViewModel.cs:11-30`
- **证据**: `MainViewModel` 创建后没有 `Dispose` 调用。窗口关闭时订阅泄漏。
- **修复方向**: `MainWindow.Closed` 事件中 `_vm?.Dispose()`。

### F-30 `OnGlobalKeyDown` 在 TextBox 焦点时仍触发全局快捷键【Medium】
- **位置**: `MainWindow.cs:880-907`
- **证据**: 未检查 `e.Source` 是否为 `TextBox`。当用户在搜索框/控制台输入框输入时，按 F2/F5/Delete/Ctrl+A 仍触发全局命令（Delete 会删除文件、Ctrl+A 全选文件而非文本）。
- **修复方向**: 方法开头 `if (e.Source is TextBox) return;`（少数纯导航键如 Alt+Left/Right/F5 可保留）。

---

## 3. 视觉显示问题（Visual）

### V-01 全局硬编码颜色，无主题适配【Critical】
- **位置**: `MainWindow.cs:60,75,379,427,617,582,566,571`；`GlobalSearchWindow.cs:75`；`QuickLookWindow.cs:78`
- **证据**:
  - `_navTree.BorderBrush = Brushes.LightGray`（L60）
  - `_fileListHeader.Background = Brushes.LightGray`（L75）
  - `addressBarBorder.Background = Brushes.White`（L379）—— 深色主题下白底刺眼
  - `statusBar.Background = Brushes.LightGray`（L427）
  - `_consolePanel.Background = new SolidColorBrush(Color.Parse("#1E1E1E"))`（L617）—— 硬编码深色
  - `_errorPanel.Background = Brushes.LightPink`（L582）
  - `consoleLabel.Foreground = Brushes.LightGray`（L612）—— 在 `#1E1E1E` 深色上勉强可读
  - `categoryText.Foreground = Brushes.DarkRed`（L566）—— 在 LightPink 上对比度差
- **修复方向**: 抽取颜色为 `DynamicResource`，定义在 App 级 `Styles` 中；用 `ThemeDictionaries` 区分 Light/Dark。

### V-02 仅 FluentTheme，无 Light/Dark/System 切换【Critical】
- **位置**: `App.cs:18-22`
- **证据**: `Styles.Add(new FluentTheme())` 后无任何 `Application.Current.RequestedThemeVariant` 设置。`IThemeService` 已注册但 `App` 与 `MainWindow` 均未使用。ADR-0013 §8 明确承诺「Light / Dark / System」+ 用户偏好持久化。

### V-03 状态栏缺少选中项大小显示【High】
- **位置**: `MainWindow.cs:423-445`；`StatusbarViewModel.cs:64-76`
- **证据**: `StatusbarViewModel` 有 `ItemCount`/`SelectedCount`/`ActiveTaskCount`，**没有 `SelectedSize`**。
- **期望**: Explorer 状态栏：「5 items selected, 12.3 MB」+「Free space: 45.2 GB」。

### V-04 状态栏 `TasksLabel` 未在 UI 显示【Medium】
- **位置**: `MainWindow.cs:423-445`；`StatusbarViewModel.cs:91`
- **证据**: `StatusbarViewModel.TasksLabel` 已实现并订阅 locale 切换，但 `MainWindow.BuildStatusBar` 中没有绑定它。

### V-05 无空状态显示【Medium】
- **位置**: `MainWindow.cs:64-68`
- **证据**: 目录为空时 `ListBox` 显示空白。无「This folder is empty」提示。

### V-06 无加载状态指示【Medium】
- **位置**: `MainWindow.cs`；`PaneViewModel.cs:95-99`
- **证据**: `PaneViewModel.IsLoading` 已实现但 `MainWindow` 从未绑定它。

### V-07 无错误状态显示【Medium】
- **位置**: `MainWindow.cs`；`PaneViewModel.cs:102-106`
- **证据**: `PaneViewModel.ErrorMessage` 已实现但未绑定到 UI。

### V-08 文件列表用 `ListBox` 而非 `DataGrid`，列宽不可调【High】
- **位置**: `MainWindow.cs:64-68`、`MainWindow.cs:71-76`、`MainWindow.cs:516-520`
- **证据**: `_fileList` 是 `ListBox`，`_fileListHeader` 是独立 `Grid`，两者用相同 `ColumnDefinitions = "24, *, 100, 80, 160"` 硬编码。列宽不可拖动调整，不持久化，header 与 item 列宽可能不同步。`Avalonia.Controls.DataGrid` 包已在 `.csproj` 引用但未使用。

### V-09 面包屑分隔符用 `>` 而非 Explorer 风格【Low】
- **位置**: `MainWindow.cs:729`
- **证据**: `Text = ">"`，外观像 CLI 重定向符。Explorer 用 `›`（U+203A）。

### V-10 工具按钮 emoji 跨平台渲染不一致【Low】
- **位置**: `MainWindow.cs:49-53`
- **证据**: `📁`、`📋`、`✂`、`🗑`、`✎` 依赖系统 emoji 字体。

### V-11 导航树节点无图标【Medium】
- **位置**: `MainWindow.cs:744-776`
- **证据**: `TreeViewItem.Header` 是纯文本。Explorer 显示 Quick access/This PC/Network/盘符图标。

### V-12 侧边栏宽度 220 固定不可调【Medium】
- **位置**: `MainWindow.cs:151`（`Width = 220`）
- **证据**: 无 `GridSplitter`。

### V-13 窗口尺寸/位置不持久化【Low】
- **位置**: `MainWindow.cs:136-137`（`Width = 1200; Height = 800`）
- **证据**: `IConfigurationService` 已注册但未用于窗口状态。

### V-14 控制台/错误面板高度固定不可调【Low】
- **位置**: `MainWindow.cs:618`（`Height = 220`）、`MainWindow.cs:583`（`Height = 150`）

### V-15 文件图标 16x16 硬编码，高 DPI 模糊【Medium】
- **位置**: `Converters/ItemIconConverter.cs:33,57`
- **证据**: `Canvas { Width = 16, Height = 16 }`。

### V-16 文件图标按 MIME prefix 区分过粗【Medium】
- **位置**: `Converters/ItemIconConverter.cs:105-119`
- **证据**: 仅按 `text/`、`image/`、`audio|video`、`application/` 分色。不区分 .pdf/.exe/.zip/.cs/.json 等。

### V-17 无窗口图标【Low】
- **位置**: `MainWindow.cs`（无 `Icon` 属性设置）

### V-18 控制台输出框是只读 `TextBox`，无颜色/搜索/自动滚动【Low】
- **位置**: `MainWindow.cs:117-125`、`MainViewModel.cs:396-417`
- **证据**: 每次执行命令用 `StringBuilder` 拼接全部历史输出，覆盖整个 `Text`。无颜色编码、无搜索、无自动滚动。

### V-19 面包屑深路径溢出无滚动【Low】
- **位置**: `MainWindow.cs:33-36`、`MainWindow.cs:689-741`

### V-20 `StatusProfileLabel` 初始文本硬编码英文【Low】
- **位置**: `MainWindow.cs:87`
- **证据**: `Text = "Loading profile..."` 硬编码（虽 `ApplyTranslations` 后变翻译文本，但构造期间短暂显示英文）。

### V-21 无 Tooltip 在导航树项显示完整路径【Low】
- **位置**: `MainWindow.cs:779-784`

### V-22 选中项视觉反馈依赖 FluentTheme 默认样式【Low】
- **位置**: `MainWindow.cs:64-68`

---

## 4. Windows Explorer 设计标准问题（Design Standards）

### D-01 顶部工具栏布局不符 Explorer Command Bar 风格【High】
- **位置**: `MainWindow.cs:372-420`（`BuildCommandBar`）
- **证据**: 下行是 `StackPanel Orientation=Horizontal` 平铺 9 个按钮 + 1 个 `Separator`。Win11 Explorer 的 Command Bar 是：图标按钮分组（New/Cut/Copy/Paste | Rename/Delete/Sort/View | ...），每组用 `Separator` 分隔，按钮以图标为主、悬停显示文本。

### D-02 菜单栏默认隐藏 + Alt 切换是 Win7 风格【Medium】
- **位置**: `MainWindow.cs:884-890`
- **证据**: Win11 Explorer **没有菜单栏**，所有功能整合到 Command Bar + 右键菜单。Alt 键应保留给助记键（如 Alt+Left 后退），不应单独消费。

### D-03 Alt 单键消费破坏助记键体系【Medium】
- **位置**: `MainWindow.cs:884-890`
- **证据**: `if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)` 后 `return`，不检查是否按了其他键。Alt+其他键的组合（如 Alt+D 地址栏聚焦）会因 Alt 单独触发而失效。
- **修复方向**: 监听 `KeyUp` 而非 `KeyDown`，或检查 `e.KeyModifiers == KeyModifiers.Alt && !e.Handled`。

### D-04 导航窗格（侧边栏）不符合 Explorer 结构【High】
- **位置**: `MainWindow.cs:744-776`
- **证据**: Win11 Explorer 导航窗格结构：Home > Quick access (pinned) > Favorites > Recent files > This PC (drives) > Network。OpenShell 是平级的 Quick access / This PC / Network 三个根节点，无 Home 概念，无 Recent files，无盘符动态枚举。

### D-05 状态栏布局不符 Explorer 习惯【Medium】
- **位置**: `MainWindow.cs:423-445`
- **证据**: 当前：`Items: N | Selected: M | Errors: K | Loading profile...`。Explorer：`N items` 或 `M items selected` + `总大小` + `可用空间`。

### D-06 右键菜单缺 Explorer 标准项【High】
- **位置**: `MainWindow.cs:448-461`
- **证据**: 见 F-17。

### D-07 无「Sort」子菜单【Medium】
- **位置**: `MainWindow.cs:448-461`、`MainWindow.cs:354-359`
- **证据**: Explorer 右键空白处有 Sort 子菜单（Name/Date/Type/Size + Ascending/Descending）。OpenShell 仅靠列头点击。

### D-08 无「View」模式切换菜单【Medium】
- **位置**: `MainWindow.cs:354-359`

### D-09 无「Open in new window/tab」【Medium】
- **位置**: `MainWindow.cs:452`

### D-10 无「Copy as path」【Medium】
- **位置**: `MainWindow.cs:448-461`

### D-11 无「Create shortcut」【Low】

### D-12 无属性侧边面板（Details Pane）【High】
- **位置**: `MainViewModel.cs:419-436`（仅 MessageBox）、`MainWindow.cs`（无属性面板）
- **证据**: ADR-0013 §1 承诺「属性面板（选中文件详情）」。当前 `PropertiesCommand` 弹 `MessageBox` 显示文本。

### D-13 无预览侧边面板（Preview Pane）【Medium】
- **位置**: `MainWindow.cs`
- **证据**: Win11 Explorer View > Preview Pane 启用右侧预览。OpenShell 仅有独立 `QuickLookWindow` 模态窗口。

### D-14 文件列表不支持框选（Marquee Selection）【Medium】
- **位置**: `MainWindow.cs:64-68`

### D-15 Shift+Click 范围选择、Ctrl+Click 切换选择未显式实现【Medium】
- **位置**: `MainWindow.cs:64-68`
- **证据**: `ListBox` 默认行为可能支持，但与 `PaneViewModel.SelectedItems` 双向绑定缺失（F-10）会破坏此功能。

### D-16 Tab 键焦点导航未优化【Low】

### D-17 拖出标签页独立窗口未实现【Medium】
- **证据**: ADR-0013 提及但未实现（依赖 F-01 多标签页）。

### D-18 工具栏无「Sort」与「View」下拉【Medium】
- **位置**: `MainWindow.cs:397-414`

### D-19 无「Invert Selection」（反向选择）【Low】

### D-20 无「Select All」/「Deselect All」菜单项【Low】
- **位置**: `MainWindow.cs:345-352`（Edit 菜单只有 SelectAll，无 Deselect）

### D-21 收藏夹/快速访问不动态【Medium】
- **位置**: `MainWindow.cs:748-758`

### D-22 历史记录面板缺失【Medium】
- **位置**: `AppBuilder.cs:157`（注释明确 GUI host 不注册 `IHistoryService`）

### D-23 文件关联打开方式不可配置【Low】
- **位置**: `MainViewModel.cs:343-367`

### D-24 工具栏按钮缺少键盘焦点视觉反馈【Low】
- **位置**: `MainWindow.cs:909-920`

### D-25 滚动时列头不同步【Medium】
- **位置**: `MainWindow.cs:71-76`、`MainWindow.cs:175-181`
- **证据**: `_fileListHeader` 与 `_fileList` 是 `Grid` 中两个独立行。`_fileList` 水平滚动时，列头不跟随滚动。

### D-26 列宽不自适应内容【Medium】
- **位置**: `MainWindow.cs:73`（`"24, *, 100, 80, 160"`）

### D-27 无列排序状态持久化【Low】
- **位置**: `PaneViewModel.cs:24-25`

### D-28 高 DPI 缩放未验证【Low】

### D-29 无 MinWidth/MinHeight 限制【Low】
- **位置**: `MainWindow.cs:136-137`

---

## 5. 已正确实现的部分（避免误改）

- **i18n 集成**：所有 View/ViewModel 通过 `T()` 翻译，订阅 `LocaleChanged` 动态刷新（T-305/T-308/T-309/T-312/T-313/T-314）。
- **ReactiveUI 命令异常订阅**：所有 `ReactiveCommand` 均有 `ThrownExceptions.Subscribe`。
- **对话框服务**：`AvaloniaDialogService` 完整实现 MessageBox/Input/FilePicker/FolderPicker/CustomDialog。
- **任务中心 VM**：`TaskCenterViewModel`/`TaskItemViewModel`/`ProgressDialogViewModel` 完整镜像 `ITaskCenter` 状态。
- **排序逻辑**：目录优先 + 升降序切换 + 同列切换方向（`PaneViewModel.cs:218-253`）符合 Explorer。
- **选中保持**：刷新后按 `Path.Display` 匹配恢复选中（`PaneViewModel.cs:172-193`）。
- **错误流订阅**：`MainViewModel` 订阅 `IErrorStream.ErrorWritten`，未读计数 + 错误面板切换。
- **Profile 加载状态**：`IsProfileLoading` 异步加载，完成后置 false。
- **QuickLook 预览多类型**：7 种渲染（`QuickLookWindow.cs:135-146`）。
- **拖拽服务实现完整**（`AvaloniaDragDropService`）：4 格式 DataObject、修饰键协商、跨 Provider 默认 Move/Copy——**但未挂接到 UI**（见 F-05/F-06）。
- **剪贴板服务实现完整**（`AvaloniaClipboardService`）：4 格式、Cut 模式粘贴后清空——**但未注册到 DI**（见 F-05）。

---

## 6. 修复优先级

### P0（阻塞核心功能，必须立即修复）
- F-10 选中状态双向绑定（否则所有文件操作失效）
- F-05 注册 IClipboardService/IDragDropService 到 DI
- F-30 OnGlobalKeyDown 排除 TextBox 焦点
- F-23 排序订阅泄漏

### P1（Explorer 核心交互）
- F-04 Ctrl+C/X/V 剪贴板快捷键
- F-07 空格 QuickLook 预览
- F-08 搜索框过滤
- F-02 新建文件夹
- F-09 Ctrl+Shift+F 全局搜索
- F-15 Ctrl+Z/Y 撤销/重做
- F-19 动态枚举磁盘
- F-22 Open 失败写入错误流

### P2（视觉与 Explorer 标准对齐）
- V-01 + V-02 主题资源化 + Light/Dark/System 切换
- V-03 状态栏选中大小
- V-05/V-06/V-07 空/加载/错误状态
- V-08 列宽可调（DataGrid 或 GridSplitter）
- D-01 Command Bar 分组化
- D-04 导航窗格 Explorer 结构
- D-06 右键菜单补全
- F-17 右键菜单补全（同 D-06）

### P3（增强体验）
- F-01 多标签页
- F-11 地址栏可编辑
- F-12 视图模式切换
- F-14 命令面板
- F-13 双窗格（决策：保留单窗格，更新 ADR）
- D-12 属性侧边面板
- D-13 预览侧边面板
- 其余 Low 项

---

## 7. 修复策略

1. **先建立合规测试基线**：在 `tests/OpenShell.Gui.Host.Tests/GuiHostComplianceTests.cs` 中为每个待实现特性写 `[Fact(Skip="pending T-XXX")]` 测试，已实现特性 `[Fact]` 必须通过。
2. **按 P0 → P1 → P2 → P3 顺序修复**：每完成一项，移除对应测试的 `Skip`，确保通过。
3. **修复中发现的 新缺陷**须新增任务 ID 并回写本审计文档。
4. **最终验证**：`dotnet build OpenShell.slnx` 0 警告 0 错误 + 全解决方案测试全绿 + 任务清单全部 `[x]`。
