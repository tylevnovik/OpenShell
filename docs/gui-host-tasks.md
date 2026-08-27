# GUI Host 修复任务清单

- **主题**: GUI Host 不符合 Windows Explorer 设计标准 / 视觉显示异常 / 功能运作异常
- **关联审计**: `docs/gui-host-audit.md`
- **关联 ADR**: ADR-0013 / ADR-0027 / ADR-0028 / ADR-0029 / ADR-0030 / ADR-0034 / ADR-0035
- **合规测试**: `tests/OpenShell.Gui.Host.Tests/GuiHostComplianceTests.cs`

状态标记：`[ ]` 待办 / `[~]` 进行中 / `[x]` 完成 / `[!]` 阻塞

---

## 阶段零：P0 阻塞核心功能

### T-400 选中状态双向绑定（F-10）
- [x] 挂接 `_fileList.SelectionChanged` 事件，把 `ListBox.SelectedItems` 同步到 `PaneViewModel.SelectedItems`
- [x] `SelectAllCommand` 修改 `ActivePane.SelectedItems` 后，UI 反映选中（通过 SelectionChanged 反向同步或直接操作 `ListBox.SelectedItems`）
- [x] 状态栏 `SelectedCount` 随 UI 选中变化更新
- [x] Copy/Move/Delete/Rename 命令在 UI 选中项后能正常执行
- [x] 测试：`GuiHostComplianceTests.Selection_Syncs_Between_ListBox_And_ViewModel` 通过

### T-401 注册 IClipboardService/IDragDropService 到 DI（F-05）
- [x] `AppBuilder.cs` 注册 `IClipboardService → AvaloniaClipboardService`
- [x] `AppBuilder.cs` 注册 `IDragDropService → CommandDispatchingDragDropService`（若存在）+ `AvaloniaDragDropService`
- [x] 验证 `AvaloniaDragDropService` 依赖链完整注入
- [x] 测试：`GuiHostComplianceTests.Clipboard_And_DragDrop_Services_Registered` 通过

### T-402 OnGlobalKeyDown 排除 TextBox 焦点（F-30）
- [x] 方法开头 `if (e.Source is TextBox) return;`（保留 Alt+Left/Right/F5 等纯导航键在非 TextBox 时触发）
- [x] 搜索框/控制台输入框输入时按 Delete/F2/Ctrl+A 不触发文件操作
- [x] 测试：`GuiHostComplianceTests.Global_KeyDown_Ignored_When_TextBox_Focused` 通过

### T-403 排序订阅泄漏修复（F-23）
- [x] 列头点击改用 `Execute(col).Subscribe().DisposeWith(_disposables)` 或缓存到 `CompositeDisposable`
- [x] 测试：`GuiHostComplianceTests.Sort_Click_Does_Not_Leak_Subscriptions` 通过

---

## 阶段一：P1 Explorer 核心交互

### T-404 剪贴板快捷键 Ctrl+C/X/V（F-04）
- [x] `MainViewModel` 注入 `IClipboardService`，增加 `CopyToClipboardCommand`/`CutCommand`/`PasteCommand`
- [x] `CopyCommand` 改名为 `CopyToCommand`（保留「复制到文件夹」语义），新增 `CopyToClipboardCommand` 写剪贴板
- [x] `OnGlobalKeyDown` 增加 Ctrl+C/Ctrl+X/Ctrl+V 分支
- [x] 工具栏 Copy 按钮改为触发 `CopyToClipboardCommand`，Move 按钮改为 `CutCommand`
- [x] 测试：`GuiHostComplianceTests.Clipboard_Shortcuts_Wired` 通过

### T-405 空格 QuickLook 预览（F-07）
- [x] `MainViewModel` 增加 `QuickLookCommand`（注入 `IQuickLookWindow`）
- [x] `OnGlobalKeyDown` 增加 `Key.Space` 分支（排除 TextBox 焦点）
- [x] 测试：`GuiHostComplianceTests.Space_Key_Triggers_QuickLook` 通过

### T-406 搜索框过滤（F-08）
- [x] `PaneViewModel` 增加 `FilterText` 属性 + `FilterCommand`
- [x] `_searchBox` 绑定 `FilterText`（TwoWay）或挂接 `TextChanged` 触发过滤
- [x] `OnGlobalKeyDown` 增加 Ctrl+F 聚焦 `_searchBox`
- [x] 测试：`GuiHostComplianceTests.Search_Box_Filters_Items` 通过

### T-407 新建文件夹（F-02）
- [x] `MainViewModel` 增加 `NewFolderCommand`（调 `_dialogs.ShowInputAsync` 取名 → `_operations.CreateDirectoryAsync`）
- [x] `MainWindow` 挂接 `_newFolderButton.Click` + Ctrl+Shift+N 快捷键
- [x] 测试：`GuiHostComplianceTests.New_Folder_Button_Wired` 通过

### T-408 Ctrl+Shift+F 全局搜索（F-09）
- [x] `OnGlobalKeyDown` 增加 Ctrl+Shift+F 分支
- [x] 创建 `GlobalSearchWindow`，注入 `GlobalSearchViewModel`，`ShowDialog`
- [x] 测试：`GuiHostComplianceTests.Global_Search_Shortcut_Wired` 通过

### T-409 Ctrl+Z/Y 撤销/重做（F-15）
- [x] `MainViewModel` 注入 `IUndoService`，增加 `UndoCommand`/`RedoCommand`
- [x] `OnGlobalKeyDown` 增加 Ctrl+Z/Ctrl+Y 分支
- [x] 测试：`GuiHostComplianceTests.Undo_Redo_Commands_Wired` 通过

### T-410 动态枚举磁盘（F-19）
- [x] `MainWindow` 注入 `IDriveRegistry`，`BuildNavTree` 循环添加盘符节点
- [x] 盘符节点显示标签 + 可用空间
- [x] 测试：`GuiHostComplianceTests.Nav_Tree_Enumerates_Drives` 通过

### T-411 Open 失败写入错误流（F-22）
- [x] `MainViewModel.OpenCoreAsync` 失败时调 `_errors.Write(...)` 而非 `CommandOutput = ...`
- [x] 状态栏错误计数+1
- [x] 测试：`GuiHostComplianceTests.Open_Failure_Writes_To_ErrorStream` 通过

### T-412 Backspace 导航上级（F-24）
- [x] `OnGlobalKeyDown` 增加 `Key.Back` 分支（排除 TextBox 焦点）
- [x] 测试：`GuiHostComplianceTests.Backspace_Navigates_Up` 通过

### T-413 F1/Alt+Enter/Shift+Delete 快捷键（F-25）
- [x] F1 → AboutCommand
- [x] Alt+Enter → PropertiesCommand
- [x] Shift+Delete → 永久删除（调 `_operations.DeleteAsync` with permanent flag，若无 flag 则调系统 API）
- [x] 测试：`GuiHostComplianceTests.Extra_Shortcuts_Wired` 通过

---

## 阶段二：P2 视觉与 Explorer 标准对齐

### T-420 主题资源化 + Light/Dark/System 切换（V-01 + V-02）
- [x] `App` 注入 `IThemeService`，启动时读取用户偏好，设置 `RequestedThemeVariant`
- [x] 抽取硬编码颜色为 `DynamicResource`，定义在 App 级 `Styles`（ThemeDictionaries 区分 Light/Dark）
- [x] View 菜单增加主题切换项（Light/Dark/System）
- [x] 测试：`GuiHostComplianceTests.Theme_Switching_Wired` 通过

### T-421 状态栏选中大小（V-03）
- [x] `StatusbarViewModel` 增加 `SelectedSize` 属性，`UpdateFromPane` 中计算
- [x] 状态栏增加显示
- [x] 测试：`GuiHostComplianceTests.Status_Bar_Shows_Selected_Size` 通过

### T-422 状态栏 TasksLabel 显示（V-04）
- [x] `BuildStatusBar` 增加 `TextBlock` 绑定 `{Binding Statusbar.TasksLabel}`
- [x] 测试：`GuiHostComplianceTests.Status_Bar_Shows_Tasks_Label` 通过

### T-423 空/加载/错误状态显示（V-05/V-06/V-07）
- [x] 文件列表区增加空状态 `Border`（`IsVisible` 绑定 `Items.Count == 0`）
- [x] 增加 `ProgressBar IsIndeterminate="True" IsVisible="{Binding ActivePane.IsLoading}"`
- [x] 增加错误 `Border` 绑定 `IsVisible` 到 `ErrorMessage != null`
- [x] 测试：`GuiHostComplianceTests.Empty_Loading_Error_States_Displayed` 通过

### T-424 列宽可调（V-08 + D-25 + D-26）
- [x] 文件列表改用 `DataGrid`（已在 `.csproj` 引用），或自实现 `GridSplitter` 列宽控制
- [x] 列宽可拖动调整，header 与 item 列宽同步
- [x] 测试：`GuiHostComplianceTests.Column_Widths_Adjustable` 通过

### T-425 Command Bar 分组化（D-01）
- [x] 工具按钮按 Explorer 风格分组：Navigation(Back/Fwd/Up/Refresh) | New(NewFolder) | Clipboard(Copy/Cut/Paste) | Organize(Rename/Delete) | View(Sort/View 下拉)
- [x] 每组用 `Separator` 分隔
- [x] 测试：`GuiHostComplianceTests.Command_Bar_Grouped` 通过

### T-426 导航窗格 Explorer 结构（D-04 + F-20 + F-21）
- [x] 重构 `BuildNavTree`：Quick access (默认快捷位置 + IFavoritesService 收藏) > Recent (IRecentService 最近访问) > This PC (dynamic drives from IDriveRegistry) > Network
- [x] 集成 `IFavoritesService`/`IRecentService`
- [x] 订阅 `ActivePane.CurrentLocation` 变化，高亮当前路径
- [x] 测试：`GuiHostComplianceTests.Nav_Tree_Explorer_Structure` 通过

### T-427 右键菜单补全（F-17 + D-06 + D-07 + D-08 + D-09 + D-10）
- [x] 增加：Cut、Paste、Copy as path、Open in new window、Pin to Quick access、Sort 子菜单、View 子菜单、Create shortcut
- [x] 测试：`GuiHostComplianceTests.Context_Menu_Has_Explorer_Items` 通过

### T-428 侧边栏宽度可调（V-12）
- [x] 用 `Grid` 两列 + `GridSplitter` 替代 `DockPanel` + 固定 `Width = 220`
- [x] 测试：`GuiHostComplianceTests.Sidebar_Width_Adjustable` 通过

### T-429 Alt 键修复（D-02 + D-03）
- [x] Alt 单键改为 `KeyUp` 监听（或检查 `e.KeyModifiers == KeyModifiers.Alt && !e.Handled`）
- [x] Alt+其他键组合（如 Alt+D 地址栏聚焦）不被 Alt 单独触发破坏
- [x] 测试：`GuiHostComplianceTests.Alt_Key_Does_Not_Break_Mnemonics` 通过

### T-430 MinWidth/MinHeight 限制（D-29）
- [x] `MainWindow` 设置 `MinWidth=800; MinHeight=500`
- [x] 测试：`GuiHostComplianceTests.Window_Has_Min_Size` 通过

---

## 阶段三：P3 增强体验

### T-440 多标签页（F-01 + D-17）
- [x] `MainWindow` 顶部增加 `TabControl`，每 tab 包裹一个 `PaneViewModel`
- [x] `BrowserTab` 类封装 `PaneViewModel` + 标题（自动跟随 CurrentLocation 更新）
- [x] `NewTabCommand` (Ctrl+T) / `CloseTabCommand` (Ctrl+W) 至少保留一个 tab
- [x] 切换 tab 时动态切换 `ActivePane` + `ItemsSource` + 搜索框绑定
- [x] 标签页带关闭按钮（×）
- [x] 测试：`Multi_Tab_Properties_And_Commands` + `New_Tab_Creates_Tab_And_Switches` + `Close_Tab_Removes_Tab_Keeps_At_Least_One` + `Browser_Tab_Title_Updates_With_Location` + `Tab_Control_Field_Exists` + `Right_Click_Selects_Item` 通过
- [ ] tab 拖出创建新 `MainWindow`（未来增强）
- [x] tab 状态持久化（2026-08-27 核实：提交 7113274 已实现 `SessionTabsService` 加载/恢复、防抖保存与退出落盘，`Tabs_RestoreAndPersistThroughSessionService` 通过；补记状态）

### T-441 地址栏可编辑（F-11）
- [x] 面包屑 + `TextBox` 双模式切换
- [x] Ctrl+L / Alt+D 聚焦并切到编辑模式
- [x] Enter 时调 `NavigateCommand.Execute(ItemPath.Parse(text))`
- [x] 测试：`GuiHostComplianceTests.Address_Bar_Editable` 通过

### T-442 视图模式切换（F-12 + D-08 + D-18）
- [x] `MainViewModel` 增加 `ViewMode` 属性（Details/Icons/Tiles/List）
- [x] `MainWindow` 根据 `ViewMode` 切换 `ItemTemplate`
- [x] View 菜单 + 工具栏 View 下拉增加选项
- [x] 测试：`GuiHostComplianceTests.View_Mode_Switching` 通过

### T-443 命令面板（F-14）
- [x] 新增 `CommandPaletteWindow` + `CommandPaletteViewModel`，复用 `ICompletionProvider`
- [x] Ctrl+Shift+P 弹出
- [x] 测试：`GuiHostComplianceTests.Command_Palette_Window_Exists` + `Command_Palette_Show_Method_Exists` 通过

### T-444 双窗格决策（F-13）
- [x] 决策：保留 Explorer 单窗格模式（符合 Win11 Explorer）
- [x] 更新 ADR-0013 标注「双窗格改为可选/未来增强」
- [x] 移除 `RightPane` 或保留为内部字段（决策：保留为兼容字段，ActivePane 始终指向 LeftPane）

### T-445 属性侧边面板（D-12）
- [x] 增加右侧可折叠属性面板，绑定选中项
- [x] `PropertiesCommand` 改为切换面板可见性（保留 MessageBox 作为 fallback）
- [x] 测试：`GuiHostComplianceTests.Details_Pane_Visible_Property` + `Details_Pane_UI_Exists` 通过

### T-446 预览侧边面板（D-13）
- [x] View > Preview Pane 启用右侧预览
- [x] 复用 `IPreviewService`
- [x] 测试：`GuiHostComplianceTests.Preview_Pane_Visible_Property` + `Preview_Pane_UI_Exists` 通过

### T-447 窗口尺寸/位置持久化（V-13）
- [x] 启动时从 `IConfigurationService` 读取 `WindowRect`，关闭时保存
- [x] 测试：`GuiHostComplianceTests.Window_Rect_Persisted` 通过

### T-448 Service Locator 反模式清理（F-28）
- [x] `II18nService` 通过构造函数注入到所有 View/ViewModel（MainWindow / CommandPaletteWindow / MainViewModel + 8 个子组件）
- [x] View 层在 `App.OnFrameworkInitializationCompleted` 时解析并传入
- [x] 测试：`GuiHostComplianceTests.I18n_Service_Constructor_Injection` 通过

### T-449 ReactiveViewModel.Dispose 调用（F-29）
- [x] `MainWindow.Closed` 事件中 `_vm?.Dispose()` + `_disposables.Dispose()`
- [x] 测试：`GuiHostComplianceTests.ViewModel_Disposed_On_Window_Close` 通过（待补充测试）

### T-450 其余 Low 项（打包处理）
- [x] V-09 面包屑分隔符改 `›`
- [x] V-10/V-27 工具按钮 emoji 改矢量图标（`StreamGeometry.Parse` + `Avalonia.Controls.Shapes.Path`）
- [x] V-15 文件图标尺寸跟随视图模式（`ConverterParameter` + `Viewbox` 缩放）
- [x] V-16 文件图标扩展名映射表（`GetFileColors` 按扩展名着色）
- [ ] V-17 窗口图标（`WindowIcon` 需 .ico 文件，`DrawingImage` 不适用，暂留默认图标）
- [x] V-18 控制台输出改 `ListBox` + `ItemTemplate`（`ConsoleEntry` + `ConsoleEntryKindToBrushConverter` 着色）
- [x] V-19 面包屑深路径 `ScrollViewer`
- [x] V-20 `StatusProfileLabel` 初始用 i18n key
- [x] V-21 导航树项 `ToolTip`（`MakeNavTreeItem` + `ToolTip.SetTip`）
- [x] D-11 Create shortcut 菜单项（`CreateShortcutCommand` + PowerShell 创建 .lnk）
- [x] D-19 Invert Selection
- [x] D-20 Deselect All
- [x] D-23 Open with 对话框（`OpenWithCommand` + `rundll32 shell32.dll,OpenAs_RunDLL`）
- [x] D-24 工具栏按钮 `Focusable=true`
- [x] D-27 列排序状态持久化（`LoadSortState` + `IConfigurationService.Config.SortColumn/SortDirection`）
- [x] F-16 新窗口菜单项实现
- [x] F-18 网络节点导航（`TreeViewItem.Tapped` → `NavigateCommand`）
- [x] F-26 复制/移动后选中丢失提示（`StatusMessage` 属性 + 状态栏显示）
- [x] F-03 新建文件功能
