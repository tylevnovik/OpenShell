# ADR-0007: 操作引擎与操作原语 API

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0001 (Provider 能力), ADR-0002 (异步流式), ADR-0020 (Undo/Redo)

## Context

M1 需要实现 `cp / mv / rm / rename / touch / mkdir` 等核心操作命令。这些操作不能直接调用单个 Provider 接口，原因：

1. **跨 Provider 操作**：`copy-item fs::C:/foo.txt zip::archive.zip/sub` 涉及两个 Provider，源端用 `IContentProvider.OpenReadAsync`，目标端需要"写入"能力——目前 Core 没有写接口。
2. **批量与原子性**：复制 1000 个文件，部分失败时如何回滚？需要统一进度 + 错误聚合。
3. **进度报告**：单文件复制可进度回调，目录递归复制需要聚合进度（已完成 N/M、当前文件名、字节进度）。
4. **可取消**：用户 Ctrl+C 时已部分完成的操作如何处理（保留已复制？回滚？）。
5. **可扩展 Undo/Redo**：M5 需要操作日志，操作 API 必须能产出"反向操作"元数据。
6. **统一约束**：覆盖文件、创建目录、删除、改属性等都属于"操作"，应有一致的契约。

PowerShell 的做法是把 Copy/Move/Delete 直接做成 Cmdlet，内部调 `IContentReader/Writer`，但 PS 没有显式"操作引擎"层，导致跨 Provider 复制逻辑分散在各 Cmdlet，且无法被 Undo 包装。

## Decision

引入 **`IOperationEngine`** 作为操作层抽象，所有 cp/mv/rm 等命令通过它执行：

```csharp
public interface IOperationEngine
{
    ValueTask<IOperationResult> CopyAsync(
        ItemPath source, ItemPath destination, CopyOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    ValueTask<IOperationResult> MoveAsync(ItemPath source, ItemPath destination, MoveOptions options, ...);
    ValueTask<IOperationResult> DeleteAsync(ItemPath path, DeleteOptions options, ...);
    ValueTask<IOperationResult> RenameAsync(ItemPath path, string newName, ...);
    ValueTask<IOperationResult> TouchAsync(ItemPath path, DateTimeOffset? time, ...);
    ValueTask<IOperationResult> CreateDirectoryAsync(ItemPath path, ...);
}
```

### 设计要点

1. **新增 `IContentWriter` 接口**（补充 ADR-0001）：
   ```csharp
   public interface IContentWriterProvider
   {
       ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken ct = default);
   }
   ```
   Provider 按需实现。FileSystem 必实现；Archive 暂不实现（写需要重新打包）；Registry 不实现（值通过 Property 写）。

2. **跨 Provider 复制**：源 `IContentProvider.OpenReadAsync` → 目标 `IContentWriterProvider.OpenWriteAsync`，Stream 中转。同 Provider 复制优先调 Provider 自己的 `CopyToAsync`（如 FileSystem 可走 `File.Copy` 优化）。

3. **`IOperationResult` 不可变 record**：
   ```csharp
   public sealed record OperationResult(
       OperationStatus Status,
       int ItemsAffected,
       long BytesTransferred,
       IReadOnlyList<OperationError> Errors,
       OperationJournalEntry? JournalEntry);
   ```
   `Status` ∈ `Success / PartialSuccess / Cancelled / Failed`。

4. **批量策略**：递归目录复制时，子文件失败不中断整体；收集到 `Errors` 列表，全部尝试完再返回 `PartialSuccess`。`Options.StopOnError` 控制是否中断。

5. **删除走 Trash**：`DeleteOptions.UseTrash = true`（默认），物理删除需显式 `--force`。Trash 路径 `~/.openshell/trash/{timestamp}/...`。这是 Undo 的基础（见 ADR-0020）。

6. **进度聚合**：`OperationProgress` 嵌套：顶层是文件数，单文件复制时压栈第二层（字节进度）。`IProgress<OperationProgress>` 是单通道，订阅者根据层级决定如何渲染。

7. **取消语义**：取消后已完成的操作不回滚（除非 `Options.RollbackOnCancel`），返回 `Cancelled` 状态。

8. **Journal 钩子**：每次成功操作产出 `OperationJournalEntry`，含反向操作所需的全部元数据（见 ADR-0020）。引擎本身不写日志，由调用方（命令）决定是否入日志。

## Alternatives Considered

1. **直接在命令里调 Provider 接口**：被否决，跨 Provider 逻辑分散，无统一进度/错误聚合，Undo 无法统一包装。
2. **PowerShell 风格 Cmdlet 内嵌逻辑**：被否决，无法跨 host 复用（GUI 也需要复制粘贴）。
3. **`System.IO.Abstractions` 风格的 IO 抽象**：被否决，抽象层次过低，不感知 Provider/Item 模型。
4. **每操作单独接口（`ICopyOperation` / `IMoveOperation`）**：被否决，接口爆炸；统一 `IOperationEngine` + Options 参数更聚合。

## Consequences

### 优势
- 跨 Provider 操作统一处理
- 批量操作的进度/错误聚合有契约保证
- 命令实现极薄（解析参数 → 调引擎 → 渲染结果）
- Undo/Redo 可在引擎外层包装（装饰器模式）
- GUI 直接复用同一引擎做拖拽复制

### 代价
- 引擎实现复杂（跨 Provider 中转、批量、进度嵌套）
- 单元测试需 mock 多个 Provider 接口
- 同 Provider 优化路径需额外分支

### 约束
- 所有操作必须支持 `CancellationToken`
- 所有操作必须返回 `IOperationResult`，不允许返回 `void`/`Task`
- `OperationError` 必须包含 `ItemPath` + `Exception` + `Phase`（在哪一步失败）
- Trash 实现独立组件 `ITrashService`，引擎依赖它而非自己管理 Trash
- 引擎实现是 `sealed class`，禁止继承，扩展通过装饰器
