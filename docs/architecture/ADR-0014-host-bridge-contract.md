# ADR-0014: 双端 Selection / Progress Bridge 完整契约

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0007 (操作引擎), ADR-0010 (Pipeline), ADR-0013 (MVVM)

## Context

M0 的 `IHost` 接口已有 `Selection` / `Progress` 占位字段，但未明确契约。M3 需要完整化，支持：

1. **GUI 选中变化通知 CLI**：用户在 GUI 双击目录，CLI 的"当前管道对象"应同步更新
2. **CLI 命令结果回推 GUI**：CLI 跑 `ls` 后，GUI 的列表应显示结果（如果 GUI 在前台）
3. **跨进程同步（M5）**：从 GUI 启动的 CLI 子进程的结果如何回传
4. **进度统一**：复制大文件时 GUI 显示进度条，CLI 显示百分比
5. **多窗口**：GUI 多个 Pane 各自的 Selection，CLI 单一 Selection
6. **过滤后 Selection**：用户筛选后，可见项 vs 原始项的 Selection 语义
7. **Out-GridView**：CLI 调用 `out-gridview` 弹 GUI 窗口，需要把对象流"送"到 GUI
8. **取消传播**：用户点取消按钮、Ctrl+C 都要触发同一 `CancellationToken`

需求约束：
- Bridge 是接口层，不依赖进程模型（同进程 / 跨进程都可用）
- 推送是单向，双向同步通过两路推送实现
- 必须支持背压（大量进度事件不能淹没订阅者）

## Decision

### 1. 完整 Bridge 接口

```csharp
public interface IHost
{
    HostKind Kind { get; }
    ItemPath CurrentLocation { get; set; }
    IObservable<IReadOnlyList<IItem>> Selection { get; }
    IProgress<OperationProgress> Progress { get; }
    IServiceProvider Services { get; }

    // 输出通道
    Task WriteOutputLineAsync(string line, CancellationToken ct = default);
    Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken ct = default);

    // 新增：输入通道（用户在 host 内的输入操作）
    IObservable<UserInputEvent> UserInput { get; }

    // 新增：取消通道
    CancellationTokenSource CommandCancellation { get; }
}

public abstract record UserInputEvent
{
    public sealed record SelectionChanged(IReadOnlyList<IItem> Selected) : UserInputEvent;
    public sealed record LocationChanged(ItemPath NewLocation) : UserInputEvent;
    public sealed record CommandRequested(string CommandLine) : UserInputEvent;
    public sealed record CancelRequested() : UserInputEvent;
}
```

### 2. Selection 模型

`Selection` 是 `BehaviorSubject<IReadOnlyList<IItem>>`：

- **GUI**：用户在 ListBox 选中变化 → 推 `SelectionChanged` → 更新 Subject
- **CLI**：上一条命令的输出列表（若 `WriteItemsAsync` 被调用）自动作为 Selection
- **空 Selection**：合法状态（启动时 / 取消选中后）

订阅者：
- Statusbar 显示选中项数 / 总大小
- Properties 面板显示首项详情
- 其他 Pane（双窗格联动）可选订阅

### 3. Progress 模型

```csharp
public readonly record struct OperationProgress(
    long Completed,
    long? Total,
    string? Status,
    bool IsCompleted = false,
    Guid OperationId = default,    // 区分并发操作
    int Depth = 0);                 // 嵌套层级，0=顶层文件数，1=单文件字节
```

- 多个并发操作通过 `OperationId` 区分
- `Depth` 用于嵌套进度：顶层是文件数进度，第二层是当前文件的字节进度
- CLI 渲染：`[####....] 4/10 files (current: foo.txt [50%])`
- GUI 渲染：进度条 + 当前文件名 + 百分比

`Progress` 是 `IProgress<T>`，但实现内部桥接到 `Subject<OperationProgress>`，多订阅者共享：

```csharp
public sealed class ProgressBridge : IProgress<OperationProgress>, IObservable<OperationProgress>
{
    private readonly Subject<OperationProgress> _subject = new();
    public void Report(OperationProgress value)
    {
        _subject.OnNext(value);
        if (value.IsCompleted) _subject.OnCompleted();
    }
    public IDisposable Subscribe(IObserver<OperationProgress> observer) => _subject.Subscribe(observer);
}
```

### 4. Out-GridView 跨 host 调用

CLI 的 `out-gridview` 命令需要弹 GUI 窗口：

- CLI host 检测 `Kind == Cli` 时，启动 GUI 子进程（IPC 见 ADR-0021）
- 通过 IPC 把对象流序列化到子进程
- 子进程 GUI 显示 DataGrid，用户操作后回传选中项到 CLI

接口层不直接处理 IPC，由 `IGuiLauncher` 服务封装：

```csharp
public interface IGuiLauncher
{
    Task<IReadOnlyList<IItem>?> ShowGridAsync(IAsyncEnumerable<IItem> items, ViewSpec spec, CancellationToken ct);
}
```

CLI 实现走 IPC，GUI 实现直接弹窗。

### 5. 跨进程同步（M5 预留）

同进程内 `Subject` 即可；跨进程需要 IPC：

- 协议：长度前缀 JSON + 命名管道（Windows）/ Unix Socket（Linux/Mac）
- 双向流：`IHost.UserInput` 与 `IHost.Selection` 都走 IPC 通道
- 序列化：`IItem` 是 record，JSON 序列化稳定；`ItemPath` 是 struct，需自定义 converter

M3 仅同进程，跨进程留到 M5（ADR-0021）。

### 6. 取消传播

`IHost.CommandCancellation` 是 `CancellationTokenSource`：

- CLI：`Console.CancelKeyPress` → `Cancel()` → `cts.Token` 传给当前命令
- GUI：取消按钮 / Esc 键 → `Cancel()`
- 命令内进一步 `CreateLinkedTokenSource` 传给子操作

新命令开始前必须重置 CTS（创建新 CTS 替换旧的，旧 token 自然失效）。

### 7. WriteItemsAsync 语义

```csharp
public async Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken ct)
{
    var collected = new List<IItem>();
    await foreach (var item in items.WithCancellation(ct))
    {
        collected.Add(item);
        RenderItemIncremental(item);   // 流式渲染
    }
    _selection.OnNext(collected);     // 完成后更新 Selection
}
```

`Selection` 在所有项到达后更新（而非逐个），保证订阅者拿到完整列表。流式渲染在 CLI 是逐行打印，GUI 是增量加到 ObservableCollection。

## Alternatives Considered

1. **事件而非 Observable**：被否决，无背压、无组合
2. **Channel<T>**：被否决，单订阅者模型，多订阅者需 fan-out
3. **共享可变状态**：被否决，并发难、无通知
4. **每 host 各自管理 Selection**：被否决，跨 host 同步失败
5. **SignalR / WebSockets 跨进程**：被否决，本地 IPC 杀鸡用牛刀

## Consequences

### 优势
- 双向数据流统一
- 进度嵌套可表达
- 同进程零开销，跨进程可扩展
- GUI 与 CLI 共用同一抽象
- 取消统一传播

### 代价
- BehaviorSubject 内存持有最后一组 Selection（大列表占内存）
- IPC 序列化开销（M5 跨进程时）
- 多订阅者需注意订阅生命周期（CompositeDisposable）

### 约束
- `IHost` 实现必须线程安全
- `Selection` 推送必须在 UI 线程（GUI）或主线程（CLI）
- `Progress.Report` 不得阻塞调用方
- `OperationId` 必须是 `Guid.NewGuid()`，不允许复用
- `IsCompleted = true` 后订阅者必须停止处理后续 Report
- `UserInput` 是 `IObservable` 而非事件，便于 Rx 链式
- 跨进程 IPC 协议（ADR-0021）必须支持 `OperationProgress` 嵌套层级
- `WriteItemsAsync` 必须在 Selection 推送前完成流式渲染（避免订阅者拿到部分列表）
- 命令开始前必须重置 `CommandCancellation`，避免上次的 token 影响新命令
