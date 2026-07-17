# ADR-0040: 事件总线与通知系统

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: Cross-cutting
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0014 (Host Bridge), ADR-0020 (Undo/Redo Journal), ADR-0021 (Command IPC), ADR-0027 (GUI 主题/快捷键), ADR-0029 (剪贴板), ADR-0030 (预览/搜索), ADR-0031 (日志), ADR-0034 (会话恢复)

## Context

ADR-0021 定义了**命令型** IPC（CLI ↔ GUI 互发命令、执行、返回结果），但很多场景需要的是**事件型**广播：

- Provider 内部发生变更（文件被外部进程修改、S3 远端对象被他人删除）→ 需要通知 CLI/GUI 刷新视图
- GUI 用户拖拽完成操作 → 需要广播给 CLI 进程使其 Prompt 提示「3 项被移走」
- 长时操作（Copy-Item 大目录）进度 → 多端订阅同一进度流
- Undo/Redo 执行后 → 所有打开的窗口需要刷新对应路径
- 配置变更（主题切换、Provider 启用）→ 多窗口同步
- Trash 自动清理、Auto-Update 触发 → 系统级通知

ADR-0021 的 IPC 是「请求-响应」语义，强行承载事件广播会：
- 同步阻塞：调用方等所有订阅者响应
- 一对一限制：难以广播给多个监听者
- 生命周期耦合：发送方需知道接收方地址
- 无背压：长时订阅会内存膨胀

ADR-0014 的 `BehaviorSubject` 是**进程内**的，跨进程无效。

需要专门的事件总线机制，覆盖进程内与跨进程，与 ADR-0021 IPC 互补。

## Decision

### 1. 事件总线抽象

```csharp
public interface IEventBus
{
    // 发布：同步返回，订阅者异步处理
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : IEvent;

    // 订阅：返回 IDisposable，dispose 取消订阅
    IDisposable Subscribe<TEvent>(IEventObserver<TEvent> observer) where TEvent : IEvent;

    // 订阅（委托便捷重载）
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : IEvent;

    // 取消订阅（用于无法 dispose 的场景）
    void Unsubscribe<TEvent>(IEventObserver<TEvent> observer) where TEvent : IEvent;
}

public interface IEvent
{
    Guid EventId { get; }            // 全局唯一
    DateTimeOffset Timestamp { get; } // 服务端时间
    string Source { get; }            // "fs", "cli", "gui:main", "operation-engine"
    string? CorrelationId { get; }    // 关联到某次操作（与 ADR-0020 Journal 协同）
}

public interface IEventObserver<in TEvent> where TEvent : IEvent
{
    ValueTask OnNextAsync(TEvent @event, CancellationToken ct);
    void OnError(Exception ex);
    void OnCompleted();
}
```

### 2. 事件类型分类

事件分三类，命名空间隔离：

```csharp
namespace OpenShell.Events;

// 2.1 Item 事件（Provider 抛出）
public abstract record ItemEvent : IEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Source { get; init; } = "";
    public string? CorrelationId { get; init; }
    public required ItemPath Path { get; init; }
}

public sealed record ItemCreatedEvent : ItemEvent;
public sealed record ItemChangedEvent : ItemEvent { public required ChangeKind Kind { get; init; } }   // Modified, Attribute, Property
public sealed record ItemRenamedEvent : ItemEvent { public required ItemPath OldPath { get; init; } }
public sealed record ItemDeletedEvent : ItemEvent;
public sealed record ContainerChangedEvent : ItemEvent;   // 子项变化

public enum ChangeKind { Modified, Attribute, Property, Size, Timestamp }

// 2.2 操作事件（Operation Engine 抛出，ADR-0007/0020）
public abstract record OperationEvent : IEvent;

public sealed record OperationStartedEvent : OperationEvent
{
    public required Guid OperationId { get; init; }
    public required string OperationName { get; init; }   // "Copy-Item"
    public required IReadOnlyList<ItemPath> Sources { get; init; }
    public required IReadOnlyList<ItemPath> Destinations { get; init; }
}

public sealed record OperationProgressEvent : OperationEvent
{
    public required Guid OperationId { get; init; }
    public required long CompletedBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required int CompletedItems { get; init; }
    public required int TotalItems { get; init; }
}

public sealed record OperationCompletedEvent : OperationEvent
{
    public required Guid OperationId { get; init; }
    public required bool Success { get; init; }
    public Exception? Error { get; init; }
}

// 2.3 系统事件
public sealed record ThemeChangedEvent : IEvent { public required string NewTheme { get; init; } }
public sealed record ProviderLoadedEvent : IEvent { public required string ProviderName { get; init; } public required Version Version { get; init; } }
public sealed record ProviderUnloadedEvent : IEvent { public required string ProviderName { get; init; }
public sealed record ConfigChangedEvent : IEvent { public required string Key { get; init; } }
public sealed record SelectionChangedEvent : IEvent { public required IReadOnlyList<ItemPath> Selection { get; init; } }
public sealed record LocationChangedEvent : IEvent { public required ItemPath NewLocation { get; init; } }
public sealed record SessionSnapshotEvent : IEvent { ... } // ADR-0034 触发
public sealed record UpdateAvailableEvent : IEvent { public required Version NewVersion { get; init; } } // ADR-0037
```

### 3. 进程内实现（InProcessEventBus）

- 默认实现，注册在 DI 容器
- 用 `System.Threading.Channels` 做队列背压
- 同步 `PublishAsync` 立即返回，订阅者线程池处理
- 异常隔离：单个订阅者抛错不影响其他

```csharp
public sealed class InProcessEventBus : IEventBus, IDisposable
{
    private readonly ConcurrentDictionary<Type, Channel<IEvent>> _channels = new();
    private readonly ConcurrentDictionary<Type, List<IEventObserver>> _observers = new();
    // ...
}
```

### 4. 跨进程事件桥（CrossProcessEventBridge）

利用 ADR-0021 的 Named Pipe / Unix Socket 通道做事件转发：

```
[CLI 进程]                              [GUI 进程]
 ItemChanged ──┐                       ┌─► ItemChanged
                │                       │
   InProcessEventBus                    │   InProcessEventBus
       │                                │       ▲
       ▼                                │       │
  CrossProcessEventBridge ──── Named Pipe ──── CrossProcessEventBridge
       │                                │       │
       ▼                                │       ▼
   Pipe Event Frame                    │    Pipe Event Frame
       │                                │       │
       ▼                                │       ▼
   Serialize + Forward                  │   Deserialize + Dispatch
```

- 每个 host 启动时建立双向 pipe
- 序列化用 `System.Text.Json`（默认）或 `MessagePack`（高性能场景）
- 事件包含类型全名，接收端用 `Type.GetType` 还原
- **过滤**：避免 A→B→A 循环转发，每个事件带 `OriginHostId`，接收方不再转发

```csharp
public sealed class CrossProcessEventBridge : IDisposable
{
    public CrossProcessEventBridge(IEventBus localBus, IIpcTransport transport, string originHostId)
    {
        _localBus = localBus;
        _transport = transport;
        _originHostId = originHostId;
        _localBus.Subscribe<IEvent>(ForwardToRemote);
        _transport.MessageReceived += DispatchFromRemote;
    }

    private async ValueTask ForwardToRemote(IEvent e, CancellationToken ct)
    {
        if (e is IRemoteRoutableEvent r && r.OriginHostId != _originHostId)
            return;   // 不要回环
        var frame = EventFrame.Serialize(e, origin: _originHostId);
        await _transport.SendAsync(frame, ct);
    }
}
```

`IRemoteRoutableEvent` 标记事件可跨进程传播，避免内部临时事件外泄。

### 5. 事件持久化（可选）

部分关键事件需要持久化（用于 ADR-0020 Operation Journal、ADR-0034 会话恢复）：

- `IPersistedEventStore` 接口：基于 SQLite 或 JSON Lines
- `Operation*Event` 默认持久化
- `Item*Event` 默认不持久化（流量大），但 Undo/Redo 操作关联的事件持久化
- 持久化订阅者异步落盘，不阻塞主流程

```csharp
public interface IPersistedEventStore
{
    ValueTask AppendAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IEvent;
    IAsyncEnumerable<TEvent> ReplayAsync<TEvent>(DateTimeOffset? since = null, CancellationToken ct = default);
    ValueTask TruncateAsync(DateTimeOffset before, CancellationToken ct = default);
}
```

### 6. 背压与限流

- 进程内：`Channel<T>.CreateBounded(capacity)`，满了丢弃最老事件 + 日志告警
- 跨进程：发送队列单独限流，单 host 1000 events/s
- 订阅者慢消费：单订阅者队列满后用 `BoundedChannelFullMode.DropOldest` + 计数器记录丢弃数
- Provider 高频事件（如 Watch 文件系统变更）需先聚合再发布：`Buffer(TimeSpan.FromMilliseconds(100))`

### 7. 命令支持

新增命令：

| 命令 | 别名 | 说明 |
|---|---|---|
| `Register-EventSubscription` | subscribe | 注册订阅（仅 CLI 调试用） |
| `Unregister-EventSubscription` | unsubscribe | 取消订阅 |
| `Get-EventLog` | gel | 查询持久化事件历史 |
| `Wait-Event` | wait-event | 阻塞等待某事件（脚本用） |
| `Start-FileWatch` | watch | 启用文件系统监听并发布事件 |

示例（脚本中订阅事件）：

```
> Register-EventSubscription -Type ItemChanged -Action {
    Write-Host "Changed: $_.Path"
  }

> Start-FileWatch fs::C:/Users/foo/Documents
Started watching. Press Ctrl+C to stop.
[12:34:01] Changed: fs::C:/Users/foo/Documents/report.docx
[12:34:03] Created: fs::C:/Users/foo/Documents/notes.txt
```

### 8. GUI 集成

- `ReactiveUI` ViewModel 通过 `WhenAnyObservable` 订阅事件流
- 列表自动刷新：订阅 `ItemChangedEvent`，匹配当前路径则刷新
- Toast 通知：`OperationCompletedEvent` 触发右下角通知
- 全局状态栏：`UpdateAvailableEvent` 显示「有新版本」徽章
- 主题切换：`ThemeChangedEvent` 广播到所有窗口，ViewModel 同步切换资源字典

### 9. 事件顺序与一致性

- 同 Provider 内事件**有序**：单线程串行发布
- 跨 Provider 事件**不保证全局顺序**：用 `CorrelationId` 关联
- `ItemCreated` → `ContainerChanged`：保证先 created 后 container
- 失败回滚：操作失败时 `OperationCompletedEvent(Success=false)` 触发订阅者回滚 UI

### 10. 与 ADR-0021 IPC 的边界

| 维度 | ADR-0021 命令 IPC | ADR-0040 事件总线 |
|---|---|---|
| 语义 | 请求-响应 | 发布-订阅 |
| 接收方 | 单一明确 | 0..N 个订阅者 |
| 阻塞 | 调用方等待结果 | 立即返回 |
| 失败 | 调用方知道 | 订阅者各自处理 |
| 典型用途 | "让 GUI 执行 Copy-Item" | "告知所有监听者 A 被修改" |

简单判断：需要返回值 → 命令 IPC；只是告知 → 事件总线。

### 11. 调试与可观测性

- 与 ADR-0031 集成：所有 `IEvent` 默认以 `Debug` 级别写入 Serilog
- `Operation*Event` 以 `Information` 级别
- 失败订阅者以 `Error` 级别
- OpenTelemetry：`EventBus.Publish` 创建 span，关联 `CorrelationId`
- 诊断包（ADR-0031）包含最近 1000 条事件快照

## Alternatives Considered

1. **用 ADR-0021 IPC 强行承载事件**：被否决，语义不匹配、广播困难
2. **用 `IObservable<T>`（Rx）替代自定义 IEventBus**：被否决，背压与异常隔离需要自实现，Rx 学习成本高
3. **进程内全用 BehaviorSubject（ADR-0014 已有）**：被否决，无跨进程能力
4. **强一致事件总线（同步所有订阅者）**：被否决，性能与可用性差
5. **每个 Provider 自带事件接口**：被否决，订阅方需知道 N 个 Provider 的事件类型
6. **直接用 OS 文件系统 Watch（FSEvents/ReadDirectoryChanges）**：被否决，跨平台差异大、无远端、无操作关联
7. **用 MessageBroker（如 NATS/RabbitMQ）外部依赖**：被否决，OpenShell 不应引入额外基础设施

## Consequences

### 优势
- 进程内/跨进程一致的事件模型
- Provider 可发布变更通知，CLI/GUI 自动刷新
- 长操作进度统一广播到所有 host
- Undo/Redo、配置变更、主题切换多窗口同步
- 可持久化用于审计与会话恢复
- 与 IPC 边界清晰，职责互补

### 代价
- 引入新抽象（IEventBus + 跨进程桥）
- 事件类型增长（需规范化命名与生命周期）
- 调试复杂（事件链路追踪）
- 背压与丢弃策略需要调优
- 跨进程序列化性能开销

### 约束
- 所有事件必须实现 `IEvent`
- 跨进程传播必须标记 `IRemoteRoutableEvent`，禁止默认传播
- 跨进程事件必须 JSON 可序列化（无闭包、无 Stream）
- 事件处理器禁止阻塞（必须 async）
- 事件 ID 必须全局唯一（Guid v7，含时间序）
- 订阅者必须 dispose 或 unsubscribe，否则内存泄漏
- 事件名禁止 `ed` 后缀之外的过去时变体（统一 `XxxEvent`，不用 `XxxNotified`/`XxxRaised`）
- 持久化事件保留 30 天后自动 truncate
- 高频事件（>100 events/s）必须聚合后发布
- 启动时事件回放（replay）必须显式调用，不自动重放
