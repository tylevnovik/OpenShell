namespace OpenShell.Operations;

/// <summary>
/// Outcome of an operation. Per ADR-0007.
/// </summary>
public enum OperationStatus
{
    /// <summary>All items succeeded.</summary>
    Success,

    /// <summary>Some items failed; see <see cref="OperationResult.Errors"/>.</summary>
    PartialSuccess,

    /// <summary>User cancelled; partial work may remain.</summary>
    Cancelled,

    /// <summary>Fatal failure before any work or with rollback applied.</summary>
    Failed,
}
