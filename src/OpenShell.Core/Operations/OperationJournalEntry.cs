using System.Text.Json.Serialization;
using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// 操作日志条目。Per ADR-0007 §8, ADR-0020 §1.
/// 操作引擎装饰器 (JournalingOperationEngine) 在每次成功操作后追加一条到 IOperationJournal,
/// 用于 Undo/Redo 反向执行。Undo = null 表示不可逆操作。
/// </summary>
public sealed record OperationJournalEntry
{
    public Guid EntryId { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Command name, e.g. "copy-item".</summary>
    public required string Operation { get; init; }

    /// <summary>Source paths (for undo: these are the targets of the reverse operation).</summary>
    public required IReadOnlyList<ItemPath> Sources { get; init; }

    /// <summary>Destination paths (for undo: these are the sources of the reverse operation).</summary>
    public required IReadOnlyList<ItemPath> Destinations { get; init; }

    /// <summary>Reverse operation command name, e.g. "move-item" reverses "move-item". Legacy field, kept for backward compat.</summary>
    public string ReverseOperation { get; init; } = string.Empty;

    /// <summary>操作参数 (如 recurse=true, trashId=...)。Per ADR-0020 §1.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>反向操作描述。null 表示不可逆 (如 Remove-Item -Force 物理删除)。Per ADR-0020 §1, §3.</summary>
    public UndoInfo? Undo { get; init; }

    /// <summary>是否已被 Undo 标记。Per ADR-0020 §8 (Undo 时标记, Redo 时取消标记)。</summary>
    public bool IsUndone { get; init; }

    /// <summary>Optional payload (e.g. original bytes for content restore).</summary>
    [JsonIgnore]
    public object? ReversePayload { get; init; }

    /// <summary>Correlation id linking to a session/command invocation.</summary>
    public string? CorrelationId { get; init; }
}
