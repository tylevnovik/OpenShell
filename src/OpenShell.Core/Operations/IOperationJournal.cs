namespace OpenShell.Operations;

/// <summary>
/// 操作日志持久化服务。Per ADR-0020 §1, §5.
/// 负责把 <see cref="OperationJournalEntry"/> 落盘到 <c>~/.openshell/journal.jsonl</c> (JSON Lines),
/// 支持读取最近 N 条、标记 Undo、清理过期记录。
/// 容量上限 10000 条 FIFO; 启动时加载最近 1000 条到内存。
/// </summary>
public interface IOperationJournal
{
    /// <summary>记录一条已执行的操作到日志 (append-only)。</summary>
    /// <param name="entry">日志条目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask AppendAsync(OperationJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>读取最近 N 条操作日志 (默认 100)。</summary>
    /// <param name="count">读取条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<OperationJournalEntry>> ReadRecentAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>标记某条 entry 为已 Undo (不再可 redo, 但保留用于审计)。</summary>
    /// <param name="entryId">日志条目 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask MarkUndoneAsync(Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>取消 Undo 标记 (Redo 时调用, 把 entry 重新标记为 active)。</summary>
    /// <param name="entryId">日志条目 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask MarkRedoneAsync(Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>清除已 Undo / 过期的记录。Per ADR-0020 §8 (新操作后清空 Redo 栈) + §5 (容量限制)。</summary>
    /// <param name="olderThan">清理超过此时间的记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    /// <summary>新操作追加事件。订阅者 (如 IUndoService) 据此清空本地 Redo 栈。Per ADR-0020 §8.</summary>
    event EventHandler<OperationJournalEntry>? Appended;
}
