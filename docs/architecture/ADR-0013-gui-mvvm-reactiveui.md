# ADR-0013: GUI Host MVVM 分层与 ReactiveUI 集成

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0003 (不可变 Item), ADR-0011 (格式化), ADR-0014 (Bridge)

## Context

M0 的 GUI 是 code-behind 拼控件的最小验证；M3 需要做成完整文件管理器：

- 双窗格（左右各一目录视图，独立 CurrentLocation）
  > **实现注记（T-444）**：当前采用 Win11 Explorer 风格单窗格模式。`RightPane` 作为兼容字段保留在 `MainViewModel` 中，`ActivePane` 始终指向 `LeftPane`。双窗格改为可选/未来增强项，不在当前 milestone 强制实现。
- 多标签页（每 tab 一个会话）
- 树形侧边栏（盘符 / 收藏 / Provider 列表）
- 工具栏（后退/前进/上/复制/粘贴/删除/属性）
- 上下文菜单（右键）
- 属性面板（选中文件详情）
- 状态栏（路径、项数、选中大小）
- 拖拽（同一 Provider 内移动、跨 Provider 复制）
- 命令面板（Ctrl+Shift+P，类 VS Code）
- 主题切换（亮/暗）

需求约束：

- ViewModel 可单测，不依赖 Avalonia 类型
- 命令逻辑通过 `ICommand`/`IPipelineCommand` 复用 Core，不重写
- 异步操作（加载目录、复制大文件）不阻塞 UI
- 大量 IItem 绑定需虚拟化（见 ADR-0015）
- 主题/布局可热切换

选型比较：

| 框架 | 评价 |
|---|---|
| ReactiveUI | 函数响应式，与 Avalonia 集成成熟，`WhenAnyValue` 链式观察，最适合 |
| CommunityToolkit.Mvvm | 简单，源生成器，但响应式弱 |
| Prism | 重，企业向，过设计 |
| 自研 MVVM | 重复造轮子 |

ReactiveUI 与 ADR-0010 的 `IObservable` 模型天然契合（`IAsyncEnumerable` 可转 `IObservable`）。

## Decision

采用 **ReactiveUI + View-first / ViewModel-first 混合**：

### 1. 分层

```
┌──────────────────────────────────────────────────┐
│ Views (.axaml + code-behind)                     │
│  MainWindow, PaneView, TreeSidebar, CommandPalette│
└────────────┬─────────────────────────────────────┘
             │ DataContext 绑定
┌────────────▼─────────────────────────────────────┐
│ ViewModels (ReactiveObject)                       │
│  MainViewModel, PaneViewModel, TreeSidebarVM,     │
│  CommandPaletteViewModel, StatusbarViewModel      │
└────────────┬─────────────────────────────────────┘
             │ 调用
┌────────────▼─────────────────────────────────────┐
│ Services (Core 注入)                              │
│  IProviderRegistry, ICommandRegistry,             │
│  IOperationEngine, IHost (GuiHost), IEventBus     │
└──────────────────────────────────────────────────┘
```

### 2. ViewModel 基类

```csharp
public abstract class ReactiveViewModel : ReactiveObject, IDisposable
{
    protected readonly CompositeDisposable Disposables = new();
    public void Dispose() => Disposables.Dispose();
}
```

### 3. 核心 ViewModel

- **MainViewModel**：管理标签页列表 `ObservableCollection<TabViewModel>`、当前活动 tab、全局状态栏、命令面板状态
- **TabViewModel**：单个会话，含两个 `PaneViewModel`（左右窗格）、活动窗格指针、历史导航栈
- **PaneViewModel**：单个目录视图，含 `CurrentLocation`、`ObservableCollection<IItem> Items`、`IItem? SelectedItem`、`IReadOnlyList<IItem> SelectedItems`、`ReactiveCommand<Unit, Unit> NavigateUp/NavigateBack/NavigateForward/Refresh`
- **TreeSidebarViewModel**：盘符/收藏/Provider 列表，点击切 tab 的 CurrentLocation
- **CommandPaletteViewModel**：Ctrl+Shift+P 弹窗，调 `ICompletionSource` 复用补全数据
- **StatusbarViewModel**：当前路径、项数、选中大小、操作进度

### 4. 命令调用

ViewModel 不直接调 Core 命令类，而是通过 `ICommandDispatcher`：

```csharp
public interface ICommandDispatcher
{
    Task<IOperationResult> InvokeAsync(string commandLine, CommandContext ctx, CancellationToken ct);
    Task<IOperationResult> InvokeAsync(CommandDescriptor desc, object args, CommandContext ctx, CancellationToken ct);
}
```

GUI 通过它复用 CLI 同款 `Get-ChildItem` / `Copy-Item` 等命令。例如双击目录：

```csharp
NavigateTo = ReactiveCommand.CreateFromTask(async (ItemPath path) =>
{
    var ctx = BuildContext(currentLocation: path);
    await _dispatcher.InvokeAsync("get-childitem", ctx, ct);
    // GetChildItemCommand 调 Host.WriteItemsAsync 把结果送到 PaneViewModel.Items
});
```

### 5. 异步与 UI 线程

- `RxApp.MainThreadScheduler` 自动切回 UI 线程
- `ReactiveCommand.CreateFromTask` 默认在 `RxApp.TaskpoolScheduler` 跑
- `ObservableCollection` 修改必须在 UI 线程，`Dispatcher.UIThread.Post`
- 长操作（复制大文件）通过 `IProgress<OperationProgress>` 推进度，StatusbarViewModel 订阅更新

### 6. 双向数据流

```
PaneViewModel.Items  ←──订阅─── IHost.Selection
        │                          
PaneViewModel.SelectedItem  ──推送──→ IHost.Selection (Subject)
                                        │
                                        └──→ 其他 Pane/CLI 窗口（M5 跨进程同步）
```

`IHost.Selection` 是 `BehaviorSubject<IReadOnlyList<IItem>>`，PaneViewModel 选中变化推到 Host，其他订阅者收到。详见 ADR-0014。

### 7. 拖拽

`DragDrop` 事件转换为 `Copy-Item` / `Move-Item` 命令调用：

- 同 Provider 拖拽默认 Move
- 跨 Provider 默认 Copy
- 按住 Shift 强制 Move，Ctrl 强制 Copy
- 拖拽到 Trash 目录 = Delete（走 ADR-0007 的 Trash）

### 8. 主题

`App.axaml` 定义 `FluentTheme` + 自定义 `ThemeDictionaries`：

- Light / Dark / System
- 用户偏好持久化到 `~/.openshell/config.toml`（见 ADR-0022）
- 启动时读取，运行时通过 `Application.Styles.Clear()` + `Add()` 切换

### 9. 测试策略

- ViewModel 单测注入 mock `ICommandDispatcher` / `IProviderRegistry`
- 不依赖 Avalonia 类型，可在 .NET 控制台测试项目跑
- View 测试用 Avalonia.Headless（无显示环境跑 UI 测试）

## Alternatives Considered

1. **CommunityToolkit.Mvvm**：被否决，源生成器 + `ObservableProperty` 简洁但响应式弱，复杂筛选/聚合难写
2. **Prism**：被否决，模块化 / 区域系统对单 app 过度设计
3. **纯 code-behind**：被否决，M0 已验证可行但难维护
4. **Caliburn.Micro**：被否决，约定式绑定隐式，重命名易碎
5. **自研 MVVM**：被否决，重复造轮子，`INotifyPropertyChanged` 触发逻辑易错

## Consequences

### 优势
- ViewModel 可单测，无 Avalonia 依赖
- `WhenAnyValue` / `Observable` 链式表达复杂依赖
- ReactiveUI 与 Avalonia 集成成熟
- 命令逻辑 100% 复用 Core
- 主题/布局可热切换
- 拖拽转命令统一处理

### 代价
- ReactiveUI 学习曲线（响应式思维）
- `ReactiveCommand` 调试堆栈深
- Avalonia + ReactiveUI 包大小（约 5MB）
- 复杂双窗格 + 多 tab 的状态机需小心设计

### 约束
- ViewModel 不引用 `Avalonia.*` 命名空间
- View 不写业务逻辑，仅绑定 + 触发命令
- 所有 ReactiveCommand 必须有 `ThrownExceptions` 订阅，否则异常静默
- `ObservableCollection` 修改必须经 `Dispatcher.UIThread.Post`
- `ICommandDispatcher` 是唯一命令入口，禁止 ViewModel 直接 `new XxxCommand()`
- 主题切换不允许丢失用户当前选中状态
- 拖拽转换的命令必须有 Undo 信息（见 ADR-0020）
- 测试项目命名 `OpenShell.Gui.Host.Tests`，目标 `net8.0` 不含 Avalonia
