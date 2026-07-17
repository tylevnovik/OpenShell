# ADR-0029: 剪贴板与拖拽抽象

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0007 (操作引擎), ADR-0014 (Bridge), ADR-0022 (配置)

## Context

GUI 与 CLI 需要剪贴板与拖拽支持：

1. **复制文件路径**：选中文件 Ctrl+C，粘贴到文本框得路径文本
2. **复制文件对象**：选中文件 Ctrl+C，粘贴到另一目录得文件副本（走 `copy-item`）
3. **剪切**：移动而非复制
4. **拖拽**：同 Provider = Move，跨 Provider = Copy
5. **拖到 Trash**：删除
6. **拖到 CLI 窗口**：插入路径文本
7. **跨应用**：从 Explorer 拖文件到 OpenShell、从 OpenShell 拖文件到 Explorer
8. **多选**：批量复制 / 拖拽
9. **取消**：长拖拽过程中 Esc 取消
10. **进度反馈**：拖拽时鼠标变化、复制时进度条

挑战：
- OS 剪贴板是文本/位图，OpenShell 需对象引用
- 跨应用拖拽需 OS 互操作（Windows OLE、X11 DnD、macOS NSPasteboard）
- 同 Provider 拖拽可优化为 `Move`（同卷 rename），跨 Provider 必须 Copy
- 删除走 Trash（ADR-0020）

## Decision

### 1. 剪贴板抽象

```csharp
public interface IClipboardService
{
    ValueTask SetItemsAsync(IReadOnlyList<IItem> items, bool cut = false, CancellationToken ct = default);
    ValueTask<IReadOnlyList<IItem>?> GetItemsAsync(CancellationToken ct = default);
    ValueTask SetTextAsync(string text, CancellationToken ct = default);
    ValueTask<string?> GetTextAsync(CancellationToken ct = default);
    bool HasItems { get; }
    bool WasCut { get; }
}
```

### 2. 剪贴板格式

OS 剪贴板同时写入多种格式：

| 格式 | 内容 | 用途 |
|---|---|---|
| `OpenShellItems` (自定义) | 序列化的 `IReadOnlyList<IItem>` JSON | 跨 OpenShell 实例粘贴 |
| `text/uri-list` | 文件路径列表（每行一个） | 跨应用（Explorer / Finder） |
| `text/plain` | 同上 | 文本框粘贴 |
| `CF_HDROP` (Windows) | Windows 文件列表结构 | Explorer 互操作 |

### 3. 文本表示

文件路径文本格式：

```
fs::C:/Users/me/file.txt
fs::C:/Users/me/another.png
```

每行一个 `ItemPath.Display`。粘贴到文本框得此格式，CLI 可直接 `paste | get-item` 解析。

### 4. Cut vs Copy

- `Copy`：剪贴板持有引用，原位置不动
- `Cut`：剪贴板标记 `WasCut = true`，原位置暂时保留；粘贴时执行 `Move-Item`，原位置删除

Cut 后用户在原位置改文件，粘贴时检测到 modification 则提示"原文件已修改，是否继续"。

### 5. 拖拽抽象

```csharp
public interface IDragDropService
{
    /// <summary>开始拖拽。target 是预期放置位置（鼠标悬停的目录）。</summary>
    Task StartDragAsync(IReadOnlyList<IItem> items, ItemPath? target, DragDropEffects effects, CancellationToken ct);

    /// <summary>接收拖入。返回实际效果。</summary>
    Task<DragDropEffects> AcceptDropAsync(ItemPath target, IReadOnlyList<IItem> items, DragDropEffects effect, CancellationToken ct);
}

[Flags]
public enum DragDropEffects
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
    Delete = 8,    // 拖到 Trash
}
```

### 6. 拖拽默认行为

| 源 → 目标 | 默认效果 | 修饰键 |
|---|---|---|
| 同 Provider 内 | Move | Shift 强制 Move，Ctrl 强制 Copy |
| 跨 Provider | Copy | Shift 强制 Move（如目标支持写） |
| 任意 → Trash | Delete | 不可修饰 |
| 任意 → CLI 窗口 | Insert Path（不复制） | - |
| 跨应用（Explorer → OpenShell） | Copy | - |
| 跨应用（OpenShell → Explorer） | Copy（仅本地 fs 路径） | - |

### 7. 拖拽视觉

- 鼠标光标按效果变化：Copy = `+`、Move = 无标记、Delete = `x`、None = `禁止` 图标
- 拖拽时浮动缩略图（首项 icon + 项数）
- 目标目录高亮（边框变色）

### 8. 拖拽转命令

`AcceptDropAsync` 内部转换为 `copy-item` / `move-item` 命令：

```csharp
public async Task<DragDropEffects> AcceptDropAsync(ItemPath target, IReadOnlyList<IItem> items, DragDropEffects effect, CancellationToken ct)
{
    var cmd = effect switch
    {
        DragDropEffects.Copy => "copy-item",
        DragDropEffects.Move => "move-item",
        DragDropEffects.Delete => "remove-item",
        _ => throw new ArgumentOutOfRangeException(nameof(effect))
    };

    foreach (var item in items)
    {
        await _dispatcher.InvokeAsync(
            $"{cmd} {item.Path.Display} {target.Display}",
            BuildContext(), ct);
    }

    return effect;
}
```

走 `ICommandDispatcher` 自动获得 Undo/Redo（ADR-0020）、进度（ADR-0014）。

### 9. 跨应用拖拽

#### Windows OLE

`IDropSource` / `IDropTarget` 互操作，Avalonia 内置 `DragDrop` 已封装。我们提供：

- `DataObject` 包含 `OpenShellItems` 自定义格式 + `text/uri-list` + `CF_HDROP`
- 接收端检测各格式优先级

#### Linux X11 / Wayland

`XDnd` 协议，`text/uri-list` 通用。

#### macOS

`NSPasteboard`，`NSFilenamesPboardType` 处理文件列表。

### 10. Trash 拖拽

拖到侧边栏 Trash 项时：

- 效果 = `Delete`
- 走 `remove-item`（默认 `UseTrash = true`）
- 可 Undo（从 Trash 恢复，见 ADR-0020）

### 11. CLI 拖拽

CLI 窗口接收拖拽时：

- 不执行复制
- 把路径文本插入到当前输入行（光标位置）
- 多个文件用空格分隔

### 12. 取消

拖拽过程中 Esc 键取消：

- 鼠标松开时不触发 `AcceptDropAsync`
- 已开始复制的长操作通过 `CancellationToken` 取消（见 ADR-0014）

### 13. 剪贴板历史

可选功能：`~/.openshell/clipboard-history.jsonl` 记录最近 20 次剪贴板操作，`Win+V` 弹出选择面板。

### 14. 安全

- 跨应用剪贴板可能含恶意路径（如 `\\malicious\share`）
- 接收时校验路径有效性，不自动执行
- 文件路径仅本机绝对路径可跨应用，远程路径（`s3://`）仅文本

## Alternatives Considered

1. **仅文本剪贴板**：被否决，无法支持文件对象复制
2. **每 Provider 自定义**：被否决，跨 Provider 操作失败
3. **OLE 完整实现**：被否决，工作量过大，Avalonia 已封装
4. **不实现拖拽，仅按钮**：被否决，体验差
5. **拖拽直接调 Stream API**：被否决，丢失 Undo / 进度

## Consequences

### 优势
- 多格式剪贴板兼容各应用
- 拖拽转命令统一处理
- Cut/Copy/Drag 一致语义
- 跨平台抽象
- Undo 集成

### 代价
- OS 互操作层复杂
- 跨应用拖拽在不同 OS 行为差异
- 自定义剪贴板格式序列化开销

### 约束
- 剪贴板写入必须包含至少 `OpenShellItems` + `text/plain` 两种格式
- 接收端必须优先解析 `OpenShellItems`，回退到 `text/uri-list`
- Cut 操作粘贴后必须清除剪贴板
- 拖拽效果必须由源 + 目标协商决定，禁止源单方面决定
- 拖拽到 CLI 窗口必须只插入文本，不执行命令
- 跨应用拖拽仅支持本地 `fs::` 路径，其他 Provider 路径仅文本
- 拖拽取消必须立即生效，不允许半完成的拖拽触发命令
- 复制大文件时必须显示进度（ADR-0014）
- 剪贴板历史可选，默认关闭
- 接收跨应用拖拽时必须显示确认对话框（避免恶意路径自动执行）
