using OpenShell.Errors;
using OpenShell.Operations;
using OpenShell.Paths;

namespace OpenShell.History;

/// <summary>
/// 内存版 <see cref="IUndoService"/> 默认实现。Per ADR-0020 §2, §7, §8.
/// 双栈维护 Undo/Redo; 新操作 Push 时清空 Redo 栈 (符合 ADR-0020 §8 约束)。
/// 默认容量 100, 超出丢弃最旧 (栈底)。
/// 注入 <see cref="IOperationEngine"/> 用于执行反向操作 (delete/move-back/restore-from-trash/rename),
/// <see cref="IOperationJournal"/> 用于持久化 Undo 状态 (MarkUndoneAsync/MarkRedoneAsync),
/// <see cref="ITrashService"/> 用于 restore-from-trash 反向操作。
/// Undo 失败时不回滚已 undo 的步骤, 写 <see cref="ErrorRecord"/> 到 <see cref="IErrorStream"/>。
/// </summary>
public sealed class InMemoryUndoService : IUndoService
{
    private const int DefaultCapacity = 100;

    private readonly IOperationEngine _engine;
    private readonly IOperationJournal _journal;
    private readonly ITrashService _trash;
    private readonly IErrorStream? _errors;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly List<OperationJournalEntry> _undoStack = new();
    private readonly List<OperationJournalEntry> _redoStack = new();

    /// <summary>构造 InMemoryUndoService。</summary>
    /// <param name="engine">操作引擎, 用于执行反向操作。</param>
    /// <param name="journal">操作日志, 用于读历史 + 持久化 Undo 状态。</param>
    /// <param name="trash">Trash 服务, 用于 restore-from-trash 反向操作。</param>
    /// <param name="errors">错误流, Undo/Redo 失败时写 ErrorRecord (可选)。</param>
    /// <param name="capacity">最大栈容量, 默认 100。</param>
    public InMemoryUndoService(
        IOperationEngine engine,
        IOperationJournal journal,
        ITrashService trash,
        IErrorStream? errors = null,
        int capacity = DefaultCapacity)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _trash = trash ?? throw new ArgumentNullException(nameof(trash));
        _errors = errors;
        _capacity = capacity > 0 ? capacity : DefaultCapacity;

        // 订阅 journal.Appended 事件, 把新 entry 加到 Undo 栈并清空 Redo 栈 (ADR-0020 §8)。
        // 这样 JournalingOperationEngine append 后, Undo 栈自动同步, 无需显式 Push。
        _journal.Appended += OnJournalAppended;

        // 跨会话恢复: 从 journal 加载最近的 active entry 到 Undo 栈。
        try
        {
            var recent = _journal.ReadRecentAsync(_capacity).AsTask().GetAwaiter().GetResult();
            foreach (var entry in recent)
            {
                if (!entry.IsUndone)
                {
                    _undoStack.Add(entry);
                }
            }
        }
        catch
        {
            // 加载失败不阻断启动, 后续 Push 会正常工作。
        }
    }

    private void OnJournalAppended(object? sender, OperationJournalEntry entry)
    {
        lock (_lock)
        {
            _undoStack.Add(entry);
            // 超出容量: 丢弃栈底最旧条目。
            if (_undoStack.Count > _capacity)
            {
                _undoStack.RemoveAt(0);
            }
            // 新操作追加后清空 Redo 栈 (ADR-0020 §8: 新操作后不能再 redo)。
            _redoStack.Clear();
        }
    }

    /// <inheritdoc />
    public bool CanUndo
    {
        get
        {
            lock (_lock)
            {
                return _undoStack.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public bool CanRedo
    {
        get
        {
            lock (_lock)
            {
                return _redoStack.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OperationJournalEntry> UndoStack
    {
        get
        {
            lock (_lock)
            {
                return _undoStack.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OperationJournalEntry> RedoStack
    {
        get
        {
            lock (_lock)
            {
                return _redoStack.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Push(OperationJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            _undoStack.Add(entry);
            if (_undoStack.Count > _capacity)
            {
                _undoStack.RemoveAt(0);
            }
            // 新操作追加后清空 Redo 栈 (ADR-0020 §8)。
            _redoStack.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<OperationJournalEntry?> UndoAsync(int steps = 1, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (steps <= 0) steps = 1;

        // 从 Undo 栈顶取 steps 条 (栈顶在末尾, 反向遍历: 栈顶先 undo)。
        List<OperationJournalEntry> toUndo = new();
        lock (_lock)
        {
            int count = Math.Min(steps, _undoStack.Count);
            for (int i = 0; i < count; i++)
            {
                var idx = _undoStack.Count - 1 - i;
                toUndo.Add(_undoStack[idx]);
            }
        }

        if (toUndo.Count == 0) return null;

        OperationJournalEntry? lastUndone = null;
        // 反向遍历: 最近 (栈顶) 先 undo。toUndo[0] 是栈顶, toUndo[1] 是栈顶-1...
        // 多步 undo 时, 栈顶先执行, 再执行更早的。
        foreach (var entry in toUndo)
        {
            // 跳过不可逆操作 (Undo = null)。
            if (entry.Undo is null)
            {
                // 仍需从 Undo 栈移除, 否则后续 Undo 会重复尝试。
                lock (_lock)
                {
                    _undoStack.Remove(entry);
                }
                continue;
            }

            try
            {
                // 抑制 journaling: 内部反向操作不应被再次追加到 journal (ADR-0020 §7), 否则污染 Undo 栈导致无限循环。
                JournalingOperationEngine.BeginSuppress();
                OperationResult reverseResult;
                try
                {
                    reverseResult = await ExecuteReverseOperationAsync(entry, ct).ConfigureAwait(false);
                }
                finally
                {
                    JournalingOperationEngine.EndSuppress();
                }

                // 检查反向操作结果: 失败时不挪到 redo 栈, 不调 MarkUndoneAsync, 写 ErrorRecord 后 break。
                // 修复原 bug: ExecuteReverseOperationAsync 丢弃 OperationResult 返回值导致失败被静默吞掉。
                if (!reverseResult.IsSuccess)
                {
                    var firstErr = reverseResult.Errors.FirstOrDefault();
                    var ex = firstErr?.Exception
                        ?? new InvalidOperationException(reverseResult.Errors.FirstOrDefault()?.Message ?? "undo failed");
                    _errors?.Write(ErrorRecord.FromException(ex,
                        operation: "undo",
                        targetPath: entry.Sources.Count > 0 ? entry.Sources[0] : null,
                        phase: ErrorPhase.Operation,
                        suggestion: "the item may have been modified externally; check path and retry"));
                    break;
                }

                // 标记 journal 中该 entry 为已 Undo。
                await _journal.MarkUndoneAsync(entry.EntryId, ct).ConfigureAwait(false);

                lock (_lock)
                {
                    _undoStack.Remove(entry);
                    _redoStack.Add(entry);
                    if (_redoStack.Count > _capacity)
                    {
                        _redoStack.RemoveAt(0);
                    }
                }
                lastUndone = entry;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Undo 失败时不回滚已 undo 的步骤 (ADR-0020 §7 约束), 仅写 ErrorRecord。
                _errors?.Write(ErrorRecord.FromException(ex,
                    operation: "undo",
                    targetPath: entry.Sources.Count > 0 ? entry.Sources[0] : null,
                    phase: ErrorPhase.Operation,
                    suggestion: "the item may have been modified externally; check path and retry"));
                // 失败后停止后续 undo (避免连锁失败)。
                break;
            }
        }

        return lastUndone;
    }

    /// <inheritdoc />
    public async Task<OperationJournalEntry?> RedoAsync(int steps = 1, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (steps <= 0) steps = 1;

        // 从 Redo 栈顶取 steps 条 (栈顶在末尾, 最近被 undo 的先 redo)。
        List<OperationJournalEntry> toRedo = new();
        lock (_lock)
        {
            int count = Math.Min(steps, _redoStack.Count);
            for (int i = 0; i < count; i++)
            {
                var idx = _redoStack.Count - 1 - i;
                toRedo.Add(_redoStack[idx]);
            }
        }

        if (toRedo.Count == 0) return null;

        OperationJournalEntry? lastRedone = null;
        foreach (var entry in toRedo)
        {
            try
            {
                // 抑制 journaling: 内部正向操作不应被再次追加到 journal (ADR-0020 §7), 否则污染 Undo 栈导致无限循环。
                JournalingOperationEngine.BeginSuppress();
                OperationResult forwardResult;
                try
                {
                    forwardResult = await ExecuteForwardOperationAsync(entry, ct).ConfigureAwait(false);
                }
                finally
                {
                    JournalingOperationEngine.EndSuppress();
                }

                // 检查正向操作结果: 失败时不挪回 undo 栈, 不调 MarkRedoneAsync, 写 ErrorRecord 后 break。
                // 修复原 bug: ExecuteForwardOperationAsync 丢弃 OperationResult 返回值导致失败被静默吞掉。
                if (!forwardResult.IsSuccess)
                {
                    var firstErr = forwardResult.Errors.FirstOrDefault();
                    var ex = firstErr?.Exception
                        ?? new InvalidOperationException(forwardResult.Errors.FirstOrDefault()?.Message ?? "redo failed");
                    _errors?.Write(ErrorRecord.FromException(ex,
                        operation: "redo",
                        targetPath: entry.Sources.Count > 0 ? entry.Sources[0] : null,
                        phase: ErrorPhase.Operation));
                    break;
                }

                // 取消 journal 中该 entry 的 Undo 标记 (重新 active)。
                await _journal.MarkRedoneAsync(entry.EntryId, ct).ConfigureAwait(false);

                lock (_lock)
                {
                    _redoStack.Remove(entry);
                    _undoStack.Add(entry);
                    if (_undoStack.Count > _capacity)
                    {
                        _undoStack.RemoveAt(0);
                    }
                }
                lastRedone = entry;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Redo 失败: 写 ErrorRecord, 不回滚已 redo 的步骤。
                _errors?.Write(ErrorRecord.FromException(ex,
                    operation: "redo",
                    targetPath: entry.Sources.Count > 0 ? entry.Sources[0] : null,
                    phase: ErrorPhase.Operation));
                break;
            }
        }

        return lastRedone;
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }

    /// <summary>
    /// 执行反向操作。Per ADR-0020 §3 反向操作映射。
    /// 支持的反向操作: delete / move-back / restore-from-trash / rename。
    /// 返回 <see cref="OperationResult"/>; 调用方必须检查 <see cref="OperationResult.IsSuccess"/>。
    /// </summary>
    private async Task<OperationResult> ExecuteReverseOperationAsync(OperationJournalEntry entry, CancellationToken ct)
    {
        if (entry.Undo is null) return OperationResult.Successful(0, 0);

        var undoOp = entry.Undo.UndoOperation;
        var undoParams = entry.Undo.UndoParameters;

        switch (undoOp)
        {
            case "delete":
                // 删除正向操作创建的文件/目录 (Copy 副本 / Touch 新建文件 / CreateDirectory 新建目录)。
                if (undoParams.TryGetValue("path", out var pathStr))
                {
                    var path = ItemPath.Parse(pathStr);
                    // UseTrash=false (物理删除, 不需要再次进 trash)。
                    // Recurse=true (若是目录, 递归删除)。
                    return await _engine.DeleteAsync(path,
                        new DeleteOptions { UseTrash = false, Recurse = true },
                        progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("undo-delete", "missing 'path' parameter in UndoInfo");

            case "move-back":
                // 把文件从 destination 移回 source。
                if (undoParams.TryGetValue("src", out var srcStr) && undoParams.TryGetValue("dst", out var dstStr))
                {
                    var src = ItemPath.Parse(srcStr);   // 当前位置 (forward 的 destination)
                    var dst = ItemPath.Parse(dstStr);   // 原位置 (forward 的 source)
                    return await _engine.MoveAsync(src, dst,
                        new MoveOptions { Force = true },
                        progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("undo-move-back", "missing 'src'/'dst' parameter in UndoInfo");

            case "restore-from-trash":
                // 从 trash 恢复 (Delete 走 trash 的反向)。
                if (undoParams.TryGetValue("trashId", out var trashIdStr) &&
                    Guid.TryParse(trashIdStr, out var trashId))
                {
                    await _trash.RestoreAsync(trashId, ct).ConfigureAwait(false);
                    // ITrashService.RestoreAsync 是 ValueTask (无返回值), 成功即视为 Successful。
                    return OperationResult.Successful(1, 0);
                }
                return OperationResult.Failed("undo-restore", "missing 'trashId' parameter in UndoInfo");

            case "rename":
                // 改回原名 (Rename 的反向)。
                if (undoParams.TryGetValue("path", out var curPathStr) &&
                    undoParams.TryGetValue("newName", out var oldName))
                {
                    var path = ItemPath.Parse(curPathStr);  // 当前路径 (rename 后)
                    return await _engine.RenameAsync(path, oldName, progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("undo-rename", "missing 'path'/'newName' parameter in UndoInfo");

            default:
                throw new NotSupportedException($"Unknown undo operation: '{undoOp}' for entry {entry.EntryId}");
        }
    }

    /// <summary>
    /// 执行正向操作 (Redo 用)。Per ADR-0020 §8.
    /// 根据 entry.Operation 重新调用 IOperationEngine 对应方法。
    /// 返回 <see cref="OperationResult"/>; 调用方必须检查 <see cref="OperationResult.IsSuccess"/>。
    /// </summary>
    private async Task<OperationResult> ExecuteForwardOperationAsync(OperationJournalEntry entry, CancellationToken ct)
    {
        switch (entry.Operation)
        {
            case "copy":
                if (entry.Sources.Count > 0 && entry.Destinations.Count > 0)
                {
                    return await _engine.CopyAsync(entry.Sources[0], entry.Destinations[0],
                        new CopyOptions { Force = true, Recurse = true },
                        progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("redo-copy", "missing source/destination in journal entry");

            case "move":
                if (entry.Sources.Count > 0 && entry.Destinations.Count > 0)
                {
                    return await _engine.MoveAsync(entry.Sources[0], entry.Destinations[0],
                        new MoveOptions { Force = true },
                        progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("redo-move", "missing source/destination in journal entry");

            case "delete":
                // Delete (走 trash) 的 redo: 重新 trash。
                // Delete (force) 不可逆, 不应出现在 Redo 栈 (Undo 跳过了 Undo=null 的项)。
                if (entry.Sources.Count > 0)
                {
                    return await _engine.DeleteAsync(entry.Sources[0],
                        new DeleteOptions { UseTrash = true, Recurse = true },
                        progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("redo-delete", "missing source in journal entry");

            case "rename":
                if (entry.Sources.Count > 0 && entry.Destinations.Count > 0)
                {
                    var newName = entry.Destinations[0].GetName();
                    return await _engine.RenameAsync(entry.Sources[0], newName, progress: null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("redo-rename", "missing source/destination in journal entry");

            case "mkdir":
                if (entry.Sources.Count > 0)
                {
                    return await _engine.CreateDirectoryAsync(entry.Sources[0], null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("redo-mkdir", "missing source in journal entry");

            case "touch":
                if (entry.Sources.Count > 0)
                {
                    return await _engine.TouchAsync(entry.Sources[0], null, ct).ConfigureAwait(false);
                }
                return OperationResult.Failed("redo-touch", "missing source in journal entry");

            default:
                throw new NotSupportedException($"Unknown forward operation: '{entry.Operation}' for entry {entry.EntryId}");
        }
    }
}
