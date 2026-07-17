# ADR-0020: 操作日志与 Undo/Redo

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M5
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0007 (操作引擎), ADR-0022 (配置持久化)

## Context

M5 需要 Undo/Redo：

- 误删文件可恢复（`undo`）
- 误改注册表值可回滚（`undo`）
- 跨 Provider 复制错位置可撤销（`undo` 把目标端的副本删掉）
- 多步撤销（`undo 5` 撤销最近 5 步）
- 重做（`redo` 恢复 undo 撤销的操作）
- 跨会话可恢复（关闭后重开，操作历史仍在）
- 操作历史可视化（GUI 显示"最近操作"列表）

需求约束：

1. **可逆操作**：Copy / Move / Delete / Rename / SetProperty 等需要明确"反向操作"
2. **不可逆操作**：`Remove-Item -Force`（物理删除，不进 Trash）、网络 IO 等明确标记 `Irreversible`
3. **跨进程持久化**：日志必须落盘
4. **顺序保证**：操作按时间顺序记录，undo 从尾部反向
5. **并发安全**：多窗口 / 多 tab 操作不能互相冲突
6. **容量限制**：日志不能无限增长
7. **隐私**：日志可能含文件名等敏感信息，需文件权限 0600

PowerShell 没有内建 Undo/Redo，靠 DSC / Snapshot 等。我们需要自研。

## Decision

### 1. IOperationJournal

```csharp
public interface IOperationJournal
{
    /// <summary>记录一条已执行的操作。返回 journal entry id。</summary>
    ValueTask<Guid> AppendAsync(OperationJournalEntry entry, CancellationToken ct);

    /// <summary>读取最近 N 条操作（默认 100）。</summary>
    ValueTask<IReadOnlyList<OperationJournalEntry>> ReadRecentAsync(int count = 100, CancellationToken ct = default);

    /// <summary>标记某条已被 undo（不再可 redo）。</summary>
    ValueTask MarkUndoneAsync(Guid entryId, CancellationToken ct);

    /// <summary>清除所有已 undo / 过期的记录。</summary>
    ValueTask PurgeAsync(TimeSpan olderThan, CancellationToken ct);
}

public sealed record OperationJournalEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Operation,                // "copy" / "move" / "delete" / "rename" / "set-property"
    ItemPath Source,
    ItemPath? Destination,
    IReadOnlyDictionary<string, string> Parameters,    // 操作参数（如 recurse=true）
    UndoInfo? Undo);                  // null = 不可逆

public sealed record UndoInfo(
    string UndoOperation,             // "delete-destination" / "restore-from-trash" / "move-back" / "set-old-property"
    IReadOnlyDictionary<string, string> UndoParameters);
```

### 2. IUndoRedoService

```csharp
public interface IUndoRedoService
{
    ValueTask<IOperationResult> UndoAsync(int steps = 1, CancellationToken ct = default);
    ValueTask<IOperationResult> RedoAsync(int steps = 1, CancellationToken ct = default);
    IObservable<JournalChangedEvent> Changed { get; }
    IReadOnlyList<OperationJournalEntry> RecentEntries { get; }
}

public sealed record JournalChangedEvent(
    JournalChangeKind Kind,           // Append / Undone / Purged
    OperationJournalEntry? Entry);
```

### 3. Undo 反向操作映射

| 正向操作 | Undo 操作 | 说明 |
|---|---|---|
| `Copy` | `Delete Destination` | 删除复制产生的副本 |
| `Move` | `Move Destination → Source` | 把文件移回原位置 |
| `Delete` (走 Trash) | `Restore from Trash` | 从 Trash 恢复 |
| `Delete` (Force, 物理) | ❌ 不可逆 | 标记 `Undo = null` |
| `Rename` | `Rename new → old` | 改回原名 |
| `SetProperty` | `SetProperty to oldValue` | 改回旧值（Registry 用） |
| `CreateDirectory` | `Delete Directory` | 删除新建的空目录（非空报错） |
| `New-Item` (文件) | `Delete File` | 删除新建文件 |
| `Remote Upload` | `Delete Remote` | 删远端副本 |
| `Remote Download` | `Delete Local` | 删本地副本 |

### 4. Trash 实现

`ITrashService`：

```csharp
public interface ITrashService
{
    ValueTask<TrashEntry> MoveToTrashAsync(ItemPath path, CancellationToken ct);
    ValueTask<IItem?> RestoreFromTrashAsync(Guid trashId, CancellationToken ct);
    ValueTask PurgeAsync(TimeSpan olderThan, CancellationToken ct);
}

public sealed record TrashEntry(
    Guid Id,
    ItemPath OriginalPath,
    string TrashPath,                  // ~/.openshell/trash/{timestamp}/{name}
    DateTimeOffset TrashedAt);
```

Trash 目录结构：

```
~/.openshell/trash/
└── 2026-07-07T15-30-00/
    ├── manifest.json               # 含 OriginalPath、TrashedAt
    └── file.txt
```

Purge 策略：默认 7 天自动清理（配置可改），启动时执行一次。

### 5. 持久化格式

`~/.opensshell/journal.jsonl`，每行一条 JSON：

```json
{"id":"guid","ts":"2026-07-07T15:30:00Z","op":"copy","src":"fs::C:/a.txt","dst":"fs::C:/b.txt","undo":{"op":"delete","params":{"path":"fs::C:/b.txt"}}}
```

- 每条 append-only，崩溃恢复简单
- 容量上限 10000 条（FIFO 淘汰）
- 启动时加载最近 1000 条到内存，超出按需读取

### 6. 操作引擎集成

ADR-0007 的 `IOperationEngine` 通过装饰器包装 `JournalingOperationEngine`：

```csharp
public sealed class JournalingOperationEngine : IOperationEngine
{
    private readonly IOperationEngine _inner;
    private readonly IOperationJournal _journal;

    public async ValueTask<IOperationResult> CopyAsync(...)
    {
        var result = await _inner.CopyAsync(...);
        if (result.Status is OperationStatus.Success or OperationStatus.PartialSuccess)
        {
            var entry = new OperationJournalEntry(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Operation: "copy",
                Source: source,
                Destination: destination,
                Parameters: new() { ["recurse"] = options.Recurse.ToString() },
                Undo: new UndoInfo("delete", new() { ["path"] = destination.Display }));
            await _journal.AppendAsync(entry, ct);
        }
        return result;
    }
}
```

### 7. Undo 流程

```csharp
public async ValueTask<IOperationResult> UndoAsync(int steps, CancellationToken ct)
{
    var entries = await _journal.ReadRecentAsync(steps, ct);
    foreach (var entry in entries.Reverse())   // 反向执行
    {
        if (entry.Undo is null) continue;       // 跳过不可逆

        var undoOp = BuildUndoOperation(entry);
        await _engine.InvokeAsync(undoOp, ct);
        await _journal.MarkUndoneAsync(entry.Id, ct);
    }
}
```

### 8. Redo 流程

Redo 仅在 Undo 后立即可用：

- Undo 时把 entry 标记 `Undone` 但不删除
- Redo 重做该 entry 的正向操作
- 任何新操作追加后，所有 `Undone` 的 entry 转为 `Purged`（不能再 redo）

### 9. GUI 集成

- 工具栏 Undo / Redo 按钮（Ctrl+Z / Ctrl+Y）
- "最近操作"面板（侧边栏），点击某条可跳到该位置或 undo
- 操作图标按 Operation 区分（复制 / 移动 / 删除）

### 10. 并发与冲突

- 多窗口同时操作：日志全局共享，append 用文件锁
- Undo 期间禁止其他操作：UI 禁用按钮 + 操作引擎互斥锁
- 文件被外部修改：Undo 失败提示"原文件已变化"，不强制

## Alternatives Considered

1. **命令模式直接记录对象**：被否决，跨进程难持久化，类型版本兼容性差
2. **Event Sourcing 全量**：被否决，过度设计，注册表 / 远程操作无法事件溯源
3. **文件系统快照（VSS）**：被否决，平台限制（仅 Windows），且粒度太粗
4. **Git 风格 commit**：被否决，用户心智负担重
5. **不实现 Undo**：被否决，误操作风险大，体验差

## Consequences

### 优势
- 误操作可恢复
- 跨会话持久化
- GUI / CLI 共用同一日志
- 不可逆操作显式标记

### 代价
- Trash 占用磁盘空间（7 天保留期）
- 日志文件可能含敏感信息（路径名）
- Undo 期间锁住操作，复杂场景体验差
- 远程操作 Undo 可能因网络失败

### 约束
- Journal 文件权限必须 0600（Unix）/ ACL 限当前用户（Windows）
- `AppendAsync` 必须原子（文件锁或单写线程）
- `Undo` 期间必须持有操作引擎互斥锁，禁止并发新操作
- `Undo` 失败时不回滚已 undo 的步骤，提示用户
- Trash 目录大小限制（默认 1GB），超限时 purge 最旧
- 不可逆操作（`Remove-Item -Force`）必须显式确认（CLI prompt / GUI 对话框）
- Journal entry 的 `Parameters` 字段值禁止含凭据（即使是 URL 也要脱敏）
- Redo 仅在 Undo 后立即可用，新操作后清空 Redo 栈
- Trash purge 必须在启动时执行一次，且定期执行（默认每天一次）
- 操作引擎装饰器链必须保证 JournalingEngine 是最外层（最先记录，最后执行 undo）
