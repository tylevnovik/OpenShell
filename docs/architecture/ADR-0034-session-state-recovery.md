# ADR-0034: 会话与状态恢复

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M5（可选）
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0022 (配置), ADR-0031 (日志), ADR-0021 (IPC)

## Context

用户场景：

1. **多会话**：同时跑 `openshell --session work` 和 `openshell --session personal`，互不干扰
2. **崩溃恢复**：异常退出后重启，恢复上次位置、历史、未完成操作
3. **GUI tab 持久化**：关闭 GUI 重开，tab 与位置保留
4. **CLI 进程孤儿**：GUI 关闭后 CLI 子进程仍在跑（ADR-0021），需检测与清理
5. **跨机器同步**（可选）：会话状态同步到云端
6. **快照**：用户主动保存"工作区快照"以便回滚

PowerShell 的 `$PROFILE` / 历史文件是会话级，但无显式会话抽象。

## Decision

### 1. 会话模型

```csharp
public sealed record Session(
    Guid Id,
    string Name,                // "work" / "personal" / 默认 "default"
    DateTimeOffset Created,
    DateTimeOffset LastActive,
    SessionState State);

public sealed record SessionState(
    ItemPath CurrentLocation,
    IReadOnlyList<ItemPath> NavigationHistory,    // 历史栈
    IReadOnlyList<TabState> Tabs,                  // GUI 多 tab
    int ActiveTabIndex);
```

### 2. 会话目录

```
~/.opensshell/sessions/
├── default.json
├── work.json
└── personal.json
```

每会话独立 JSON 文件。

### 3. 会话生命周期

```
启动 → 加载或创建会话 → 运行 → 定期保存（30s）→ 退出 → 保存
                                    ↓
                                崩溃检测
                                    ↓
                          下次启动提示恢复
```

### 4. 崩溃检测

启动时检查 `~/.openshell/sessions/<name>.lock`：

- 锁存在 + 持有进程存活 → 提示"会话已在运行"，避免重复
- 锁存在 + 持有进程已死 → 提示"上次会话异常退出，是否恢复"

锁文件含：

```json
{"pid": 12345, "started": "2026-07-07T15:00:00Z", "machine": "laptop-abc"}
```

### 5. 状态保存内容

| 数据 | 保存频率 | 内容 |
|---|---|---|
| CurrentLocation | 每次切换 | ItemPath |
| 导航历史 | 每次切换 | 最近 100 项 |
| GUI tabs | 每次新建/关闭 | tab 位置 |
| 选中的列表项 | 不保存（隐私） | - |
| 命令历史 | 每条命令 | 见 ADR-0022 |
| 操作日志 | 每操作 | 见 ADR-0020 |
| 未完成操作 | 实时 | multipart upload ID 等 |

### 6. 恢复流程

启动时：

1. 加载会话 JSON
2. 校验路径有效性（远程可能失效）
3. 恢复 CurrentLocation（无效则降级到 home）
4. 恢复导航历史
5. 恢复 GUI tabs
6. 检测未完成操作（multipart upload ID），提示续传或清理

### 7. 多会话切换

CLI：

```
openshell-cli --session work
```

GUI：

- 多窗口模式（每会话一窗）
- 单窗口多 tab（tab 分组按会话）

### 8. 快照

`save-snapshot <name>` 命令：

- 复制当前会话状态到 `~/.opensshell/snapshots/<name>.json`
- 含位置 / 历史 / tabs
- 不含操作日志 / 历史（快照专注 UI 状态）

`restore-snapshot <name>`：

- 加载快照覆盖当前会话状态
- 不影响操作日志

### 9. 跨机器同步（可选）

`config.toml`：

```toml
[sync]
enabled = false
backend = "nextcloud"           # nextcloud / webdav / s3
path = "webdav::https://nc.example.com/dav/openshell-sessions/"
```

同步内容：会话 JSON / 快照。不同步：操作日志、历史（隐私敏感）。

冲突解决：最后写入胜出（last-write-wins），用户可强制拉 / 推。

### 10. 锁清理

僵尸锁清理：

- 启动时检测锁文件指向的 PID 是否存活
- 不存活则清理锁
- 启动时新写锁，覆盖旧锁（带提示）

### 11. GUI tab 持久化

每个 GUI tab：

```csharp
public sealed record TabState(
    Guid Id,
    string Label,
    PaneState LeftPane,
    PaneState RightPane,
    bool IsSplitView);

public sealed record PaneState(
    ItemPath CurrentLocation,
    ViewSpec? CustomView,
    SortSpec? Sort);
```

关闭 GUI 时保存 tabs，重开恢复。

### 12. CLI 子进程清理

GUI 关闭后：

- 通过 IPC（ADR-0021）发 `IpcShutdown` 给所有 CLI 子进程
- 子进程 5 秒内退出
- 未退出的进程由 GUI 启动时的进程组管理（`Job Object` Windows / `setpgid` Linux）

### 13. 隐私

- 会话状态含路径，可能敏感
- 文件权限 0600
- `clear-session` 命令清除指定会话
- 跨机器同步默认关闭

### 14. 性能

- 状态保存 < 50ms（JSON 序列化 + 文件写）
- 加载 < 100ms
- 后台异步保存，不阻塞 UI
- 频繁保存时合并（30s 节流）

## Alternatives Considered

1. **仅进程内状态，崩溃丢失**：被否决，用户体验差
2. **每次操作保存**：被否决，IO 开销大
3. **SQLite 存储**：被否决，单文件 JSON 易备份
4. **Windows Registry 存状态**：被否决，跨平台难
5. **完整事务日志（Event Sourcing）**：被否决，过度设计
6. **不实现多会话**：被否决，工作/生活分离是常见需求

## Consequences

### 优势
- 崩溃可恢复
- 多会话支持
- 快照便于回滚
- 跨机器同步（可选）
- 多 tab 持久化

### 代价
- 状态文件管理
- 锁清理逻辑
- 跨机器同步冲突解决
- 多会话内存占用

### 约束
- 会话文件权限 0600
- 锁文件必须含 PID 与机器名，便于跨机器检测
- 崩溃恢复必须可拒绝（用户选"开始新会话"）
- 跨机器同步默认关闭
- 同步冲突时必须提示用户，不静默覆盖
- 状态保存必须异步，不阻塞主流程
- 远程路径恢复失败时降级到本地 home，不阻断启动
- 多会话同名禁止（启动时检测）
- CLI 子进程清理必须等待 5s，超时记录 warning 不强制 kill
- 快照恢复不影响操作日志与历史
- 状态文件损坏时降级到默认状态，记录 error
