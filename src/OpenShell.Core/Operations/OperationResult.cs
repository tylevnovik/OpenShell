namespace OpenShell.Operations;

/// <summary>
/// Immutable result of an operation. Per ADR-0007.
/// </summary>
public sealed record OperationResult
{
    public required OperationStatus Status { get; init; }

    public int ItemsAffected { get; init; }

    public long BytesTransferred { get; init; }

    public IReadOnlyList<OperationError> Errors { get; init; } = Array.Empty<OperationError>();

    /// <summary>Optional journal entry for undo/redo (ADR-0020). Null if not journalled.</summary>
    public OperationJournalEntry? JournalEntry { get; init; }

    public bool IsSuccess => Status is OperationStatus.Success or OperationStatus.PartialSuccess;

    public static OperationResult Successful(int itemsAffected = 0, long bytes = 0, OperationJournalEntry? journal = null)
        => new()
        {
            Status = OperationStatus.Success,
            ItemsAffected = itemsAffected,
            BytesTransferred = bytes,
            JournalEntry = journal,
        };

    public static OperationResult Partial(int itemsAffected, long bytes, IReadOnlyList<OperationError> errors)
        => new()
        {
            Status = OperationStatus.PartialSuccess,
            ItemsAffected = itemsAffected,
            BytesTransferred = bytes,
            Errors = errors,
        };

    public static OperationResult Cancelled(int itemsAffected = 0, long bytes = 0)
        => new()
        {
            Status = OperationStatus.Cancelled,
            ItemsAffected = itemsAffected,
            BytesTransferred = bytes,
        };

    public static OperationResult Failed(string phase, string message, Exception? ex = null)
        => new()
        {
            Status = OperationStatus.Failed,
            Errors = new[]
            {
                new OperationError
                {
                    Path = new() { Provider = "unknown", InternalPath = "" },
                    Phase = phase,
                    Message = message,
                    Exception = ex,
                },
            },
        };
}

/// <summary>
/// 包装失败的 <see cref="OperationResult"/> 抛出给任务句柄。Per ADR-0044 §2.
/// 当 BeginXxx 后台执行返回非成功、非取消的 OperationResult 时, 用此异常包装后 MarkFailed。
/// </summary>
public sealed class OperationException : Exception
{
    /// <summary>原始操作结果 (携带 Errors 详情)。</summary>
    public OperationResult Result { get; }

    public OperationException(OperationResult result)
        : base(result.Errors.Count > 0
            ? string.Join("; ", result.Errors.Select(e => $"{e.Phase}: {e.Message}"))
            : result.Status.ToString())
        => Result = result;
}
