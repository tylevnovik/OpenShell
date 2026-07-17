using OpenShell.Operations;
using OpenShell.Paths;

namespace OpenShell.History;

/// <summary>
/// Undo/Redo 服务。Per ADR-0020 §2, §7, §8.
/// 维护 Undo/Redo 双栈; 新操作 Push 到 Undo 栈并清空 Redo 栈。
/// UndoAsync 执行真实反向操作 (delete/move-back/restore-from-trash/rename);
/// RedoAsync 重新执行正向操作。
/// 失败时不回滚已 undo 的步骤, 写 ErrorRecord 到 IErrorStream。
/// </summary>
public interface IUndoService
{
    /// <summary>是否有可撤销的操作。</summary>
    bool CanUndo { get; }

    /// <summary>是否有可重做的操作。</summary>
    bool CanRedo { get; }

    /// <summary>Undo 栈 (栈顶在末尾), 只读视图。</summary>
    IReadOnlyList<OperationJournalEntry> UndoStack { get; }

    /// <summary>Redo 栈 (栈顶在末尾), 只读视图。</summary>
    IReadOnlyList<OperationJournalEntry> RedoStack { get; }

    /// <summary>推入一条已执行的操作到 Undo 栈, 并清空 Redo 栈。</summary>
    /// <param name="entry">操作日志条目。</param>
    void Push(OperationJournalEntry entry);

    /// <summary>
    /// 撤销最近 <paramref name="steps"/> 条操作。Per ADR-0020 §7.
    /// 从 journal 读最近 N 条 entry, 反向遍历, 跳过 Undo=null 的不可逆项,
    /// 对每条调对应的反向操作 (delete/move-back/restore-from-trash/rename)。
    /// 失败时不回滚已 undo 的步骤, 写 ErrorRecord 到 IErrorStream。
    /// </summary>
    /// <param name="steps">撤销步数, 默认 1。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>最后被撤销的条目; 无可撤销时返回 null。</returns>
    Task<OperationJournalEntry?> UndoAsync(int steps = 1, CancellationToken ct = default);

    /// <summary>
    /// 重做最近 <paramref name="steps"/> 条被撤销的操作。Per ADR-0020 §8.
    /// 重新执行正向操作。
    /// </summary>
    /// <param name="steps">重做步数, 默认 1。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>最后被重做的条目; 无可重做时返回 null。</returns>
    Task<OperationJournalEntry?> RedoAsync(int steps = 1, CancellationToken ct = default);

    /// <summary>清空 Undo/Redo 双栈。</summary>
    void Clear();
}
