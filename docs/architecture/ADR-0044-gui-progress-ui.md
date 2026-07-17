# ADR-0044: GUI 进度报告 UI

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0007 (操作引擎), ADR-0013 (GUI MVVM), ADR-0014 (Host Bridge), ADR-0027 (主题/快捷键), ADR-0040 (事件总线), ADR-0043 (对话框服务)

## Context

ADR-0007 的操作引擎已建立 `IProgress<OperationProgress>` 推送机制（含 `completedItems / totalItems / bytesTransferred / totalBytes / currentPath / IsCompleted`），但当前仅在 CLI 端通过 `StatusbarViewModel` 订阅显示。GUI 端需要更丰富的进度 UI：

- 弹出进度对话框（modal 或 modeless）
- 状态栏小型进度条（背景任务）
- 任务中心（Task Center，类 VS Code，多并发任务列表）
- 取消 / 暂停 / 后台运行
- 操作历史（已完成的最近 N 条）

### 痛点

当前 M1 的 `OperationProgress` 通过 `Subject<OperationProgress>` 走 ReactiveUI，但存在以下缺口：

1. **没有"任务"概念**：一次 `Copy-Item` 是一个 Task，应有自己的 ID / 状态 / 历史，目前缺统一抽象。
2. **没有取消 / 暂停机制**：`CancellationToken` 已存在，但 UI 怎么触发取消？暂停完全缺失。
3. **没有"后台运行 + 完成后通知"**：长任务无法转入后台，且完成后没有统一通知。
4. **进度对话框样式与主题（ADR-0027）不一致**：自绘弹窗未走主题系统，亮/暗切换错乱。
5. **多并发操作 UI 怎么呈现**：同时复制 3 个目录时，单一进度条无法表达多任务。

### 设计原则

- **任务抽象**：引入 `ITaskHandle` 表示一次操作的生命周期。
- **复用 Core**：操作引擎在创建任务时返回 `ITaskHandle`，ViewModel 订阅其 Progress 事件。
- **可取消**：UI 取消按钮调 `ITaskHandle.CancelAsync()`，触发 `CancellationTokenSource.Cancel`。
- **可后台**：modal 对话框"Background"按钮关闭对话框但任务继续，状态栏显示活动任务数。
- **统一通知**：完成后通过 ADR-0040 `IEventBus` 推 `OperationCompletedEvent`，通知中心显示 Toast。

## Decision

### 1. 任务抽象（ITaskHandle / ITaskCenter）

引入任务生命周期句柄与任务中心：

```csharp
namespace OpenShell.Operations;

/// <summary>
/// 一次操作的生命周期句柄。Per ADR-0044.
/// </summary>
public interface ITaskHandle : IAsyncDisposable
{
    Guid TaskId { get; }
    string Operation { get; }           // "copy", "move", "delete", ...
    string DisplayLabel { get; }        // "Copying 3 items to fs::D:/Backup"
    TaskState State { get; }            // Pending / Running / Paused / Completed / Failed / Cancelled
    OperationProgress? LastProgress { get; }
    Exception? Exception { get; }       // 失败时的异常
    DateTimeOffset StartedAt { get; }
    DateTimeOffset? CompletedAt { get; }
    CancellationToken CancellationToken { get; }

    event EventHandler<OperationProgress>? ProgressChanged;
    event EventHandler<TaskState>? StateChanged;

    /// <summary>请求取消。返回后任务最终进入 Cancelled 状态。</summary>
    Task CancelAsync();

    /// <summary>暂停（如果操作支持，如复制；删除/重命名不支持）。</summary>
    Task PauseAsync();

    /// <summary>恢复。</summary>
    Task ResumeAsync();
}

public enum TaskState { Pending, Running, Paused, Completed, Failed, Cancelled }

/// <summary>
/// 任务中心。Per ADR-0044. 维护当前活动 + 最近完成任务列表。
/// </summary>
public interface ITaskCenter
{
    IReadOnlyList<ITaskHandle> ActiveTasks { get; }
    IReadOnlyList<ITaskHandle> RecentCompleted { get; }    // 默认 50 条
    event EventHandler<ITaskHandle>? TaskAdded;
    event EventHandler<ITaskHandle>? TaskRemoved;

    /// <summary>注册新任务，返回句柄。操作引擎内部调用。</summary>
    ITaskHandle Register(TaskRegistration registration);

    /// <summary>按 ID 查找。</summary>
    ITaskHandle? Find(Guid taskId);
}

public sealed record TaskRegistration
{
    public required string Operation { get; init; }
    public required string DisplayLabel { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public bool SupportsPause { get; init; } = false;
    public bool RunInBackgroundByDefault { get; init; } = false;
}
```

### 2. OperationEngine 集成

ADR-0007 已有 `IOperationEngine` 接口，本 ADR 是其演进，**不破坏 M1 已有签名**。采用新增 `BeginXxx` 方法的双轨制：

```csharp
public interface IOperationEngine
{
    // 旧（保留，向后兼容）
    Task<OperationResult> CopyAsync(ItemPath source, ItemPath destination,
        CopyOptions? options = null, CancellationToken ct = default);

    // 新（ADR-0044）—— 返回任务句柄，不阻塞调用方
    ITaskHandle BeginCopy(ItemPath source, ItemPath destination,
        CopyOptions? options = null, CancellationToken ct = default);

    // 同理：BeginMove / BeginDelete / BeginRename / BeginTouch / BeginCreateDirectory
}
```

旧方法内部仍可用新机制实现：`CopyAsync` 内部调 `BeginCopy` 然后 `await handle` 完成（`ITaskHandle` 配合 `IAsyncDisposable` 的 awaiter 语义）。

### 3. GUI ViewModel

```csharp
public class TaskCenterViewModel : ReactiveViewModel
{
    private readonly ITaskCenter _center;

    public ObservableCollection<TaskItemViewModel> Active { get; } = new();
    public ObservableCollection<TaskItemViewModel> Completed { get; } = new();

    public TaskCenterViewModel(ITaskCenter center)
    {
        _center = center;
        _center.TaskAdded += (s, t) =>
            Dispatcher.UIThread.Post(() => Active.Add(new TaskItemViewModel(t)));
        _center.TaskRemoved += (s, t) => Dispatcher.UIThread.Post(() =>
        {
            Active.RemoveWhere(x => x.TaskId == t.TaskId);
            Completed.Insert(0, new TaskItemViewModel(t));
            if (Completed.Count > 50) Completed.RemoveAt(Completed.Count);
        });
    }
}

public class TaskItemViewModel : ReactiveViewModel
{
    private readonly ITaskHandle _handle;

    public string DisplayLabel => _handle.DisplayLabel;
    public double ProgressPercent => ComputePercent(_handle.LastProgress);
    public TaskState State => _handle.State;
    public ReactiveCommand<Unit, Unit> Cancel =>
        ReactiveCommand.CreateFromTask(_handle.CancelAsync);
    public ReactiveCommand<Unit, Unit> Background =>
        ReactiveCommand.Create(() => { /* hide from Active list */ });

    public TaskItemViewModel(ITaskHandle handle)
    {
        _handle = handle;
        _handle.ProgressChanged += (s, p) => Dispatcher.UIThread.Post(() =>
        {
            this.RaisePropertyChanged(nameof(ProgressPercent));
            this.RaisePropertyChanged(nameof(LastProgress));
        });
        _handle.StateChanged += (s, st) =>
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(State)));
    }
}
```

### 4. 进度对话框（modal 进度弹窗）

```csharp
public class ProgressDialogViewModel : ReactiveViewModel
{
    private readonly ITaskHandle _handle;

    public string Title => _handle.DisplayLabel;
    public string CurrentPath => _handle.LastProgress?.CurrentPath ?? "";
    public double Percent => ComputePercent(_handle.LastProgress);
    public string BytesPerSec => ComputeSpeed();        // 滑动窗口
    public string EstimatedRemaining => ComputeEta();
    public ReactiveCommand<Unit, Unit> Cancel => /* ... */;
    public ReactiveCommand<Unit, Unit> Background =>
        ReactiveCommand.Create(() => RequestClose(dialogResult: null));
}
```

样式与主题（ADR-0027）严格对齐：颜色、字体、按钮样式全部从 `ThemeDictionaries` 取，亮/暗切换零代码改动。对话框通过 ADR-0043 的对话框服务呈现。

### 5. 状态栏集成

ADR-0013 `StatusbarViewModel` 新增活动任务指示：

- 活动任务数 > 0 时显示 `任务 N ⏵` + 一个小型进度条（最近一个任务）
- 点击弹出 Task Center

```csharp
// StatusbarViewModel
this.WhenAnyObservable(x => x._center.TaskAdded, x => x._center.TaskRemoved)
    .Subscribe(_ => ActiveTaskCount = _center.ActiveTasks.Count);
```

### 6. 任务中心面板

快捷键 `Ctrl+Shift+J`（与 ADR-0027 快捷键表登记），类 VS Code：

- **上半**：活动任务列表（可取消 / 暂停 / 恢复 / 后台）
- **下半**：已完成任务历史（最多 50，可清空）
- 失败任务红色高亮，双击展开错误详情（用 ADR-0043 MessageBox）

### 7. 完成通知（与 ADR-0040 联动）

任务进入终态时，由 `ITaskHandle` 的内部实现发布 `OperationCompletedEvent`：

```csharp
_handle.StateChanged += (s, state) =>
{
    if (state == TaskState.Completed)
        _eventBus.Publish(new OperationCompletedEvent(
            _handle.TaskId, _handle.Operation, success: true));
    else if (state == TaskState.Failed)
        _eventBus.Publish(new OperationCompletedEvent(
            _handle.TaskId, _handle.Operation, success: false, _handle.Exception));
};
```

通知中心（ADR-0040 `CrossProcessEventBridge`）订阅 `OperationCompletedEvent` 显示 Toast，CLI 进程也会收到。

### 8. 取消 / 暂停的实现

- **取消**：`_cts.Cancel()` 触发 OperationEngine 内部所有 `await ct` 检查点；正在执行的低层 IO（`FileStream.ReadAsync`）通过传入的 `ct` 自动取消。
- **暂停**（仅 Copy / Move 支持）：

```csharp
public async Task<OperationResult> CopyAsync(...)
{
    while (...)
    {
        await _pauseSignal.WaitAsync(ct);  // TaskCompletionSource，PauseAsync 设 false
        // ... copy chunk
    }
}
```

`_pauseSignal` 是一个内部 `PauseSignal`（基于 `TaskCompletionSource<bool>`），`PauseAsync` 设为阻塞，`ResumeAsync` 设为通过。

### 9. 多并发控制

操作引擎内部用 `SemaphoreSlim` 控制并发数（默认 4，配置项 `operations.maxParallel = 4`）。多任务时 `TaskCenterViewModel` 自然按添加顺序显示，无需额外排序。

### 10. 进度采样节流

高频 `ProgressChanged` 事件（如每复制 1KB 推一次）会导致 UI 抖动。采用 **`ITaskHandle` 内部采样**（推荐，避免每个 ViewModel 都要节流）：

- 内部维护 `DateTimeOffset _lastEmit`
- 上次推送 < 50ms 内的进度更新只刷新 `LastProgress` 字段不触发事件
- 完成时（`IsCompleted = true`）强制推送一次最终值
- 采样频率 ≤ 20 Hz（即 50ms 一次）

ViewModel 端不再额外节流。备选方案（被否决）：每个 ViewModel 自己 `Throttle(50ms)`，重复节流逻辑、易遗漏。

### 11. CLI 端退化

`CliHost` 在执行 `copy-item` 时：

- 用户没指定 `--background`：同步阻塞直到完成（与 M1 行为一致）
- `Ctrl+C` 触发 `CancelAsync()`
- 未来可支持 `&` 后台运行（参考 bash），后台任务通过 `Get-OperationLog` 查状态

### 12. 持久化策略

`TaskCenter.RecentCompleted` 仅在内存，不持久化（重启后清空）。如果用户需要历史用 `Get-OperationLog` 命令查 ADR-0022 `journal.jsonl`。

## Alternatives Considered

1. **保留 `IProgress<T>` 不引入任务抽象**：被否决，无任务生命周期管理，无法表达"取消 / 暂停 / 后台 / 历史"等 UI 诉求，多并发任务无法统一呈现。
2. **用 `System.Threading.Tasks.Task` 直接表示操作**：被否决，`Task` 缺 `State / Cancel / Pause` 语义，无法承载 `DisplayLabel / StartedAt / Exception` 等业务字段，UI 绑定困难。
3. **操作引擎全部改为返回 `ITaskHandle`**（破坏旧签名）：被否决，破坏 ADR-0007 向后兼容性，CLI 端同步语义被打破，迁移代价大；采用双轨制（新增 `BeginXxx`）规避。
4. **每个 ViewModel 自行 `Throttle` 节流进度**：被否决，重复节流逻辑、易遗漏，且采样频率不统一；改在 `ITaskHandle` 内部统一采样。
5. **`RecentCompleted` 持久化到磁盘**：被否决，与 ADR-0022 `journal.jsonl` 职责重叠，重启后清空可接受，需要历史查 journal。

## Consequences

### 优势

- 任务生命周期完整：Pending → Running → Paused → Completed / Failed / Cancelled 全状态可观测
- 可取消 / 可暂停（Copy/Move）/ 可后台运行，UI 操作直观
- 与 ADR-0040 通知联动：完成后 Toast 自动呈现，跨进程同步
- 与 ADR-0027 主题系统对齐：进度对话框与状态栏样式统一
- CLI 退化兼容：旧 `CopyAsync` 签名保留，`--background` 可选启用后台模式
- 双轨制 API 不破坏 M1 既有调用方

### 代价

- 引入额外抽象层（`ITaskHandle` / `ITaskCenter` / `TaskRegistration`），Core 体积增加
- `ITaskHandle` 与 `OperationResult` 双轨制需文档化（旧 `XxxAsync` 返回 result，新 `BeginXxx` 返回 handle），用户需理解何时用哪个
- 暂停只对部分操作生效（Copy / Move 支持，Delete / Rename / Touch / CreateDirectory 不支持），需在 UI 上根据 `SupportsPause` 显式禁用按钮
- 多并发场景下任务列表 UI 复杂度上升

### 约束

- `ITaskHandle` 必须 `Dispose`（实现 `IAsyncDisposable`），否则 `CancellationTokenSource` 与事件订阅泄漏
- `TaskCenter.RecentCompleted` 最多 50 条，超出后 FIFO 丢弃
- 进度事件采样频率 ≤ 20 Hz（`ITaskHandle` 内部统一节流，ViewModel 端不再节流）
- `Pause` / `Resume` 仅 Copy / Move 支持，其他操作调用 `PauseAsync` 抛 `NotSupportedException`
- 取消必须可重入，不抛异常（重复调 `CancelAsync` 返回已完成的 Task）
- ViewModel 订阅 `ProgressChanged` / `StateChanged` 必须在 dispose 时解绑，否则内存泄漏
- `BeginXxx` 返回的 `ITaskHandle` 立即注册到 `ITaskCenter`，调用方可订阅但不可绕过中心直接驱动状态机
- 主题相关样式必须从 `ThemeDictionaries` 取，禁止硬编码颜色
