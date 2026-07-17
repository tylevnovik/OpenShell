using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// Per-item error in a batch operation. Per ADR-0007.
/// </summary>
public sealed record OperationError
{
    public required ItemPath Path { get; init; }

    /// <summary>Phase when the error occurred.</summary>
    public required string Phase { get; init; }

    public required string Message { get; init; }

    public Exception? Exception { get; init; }

    public override string ToString() => $"[{Phase}] {Path.Display}: {Message}";
}
