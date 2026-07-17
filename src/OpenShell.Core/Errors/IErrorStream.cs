namespace OpenShell.Errors;

/// <summary>
/// Structured error sink. Per ADR-0026.
/// CLI impl writes to stderr with ANSI red; GUI impl pushes to a status bar / error panel.
/// </summary>
public interface IErrorStream
{
    void Write(ErrorRecord error);

    /// <summary>Last error written, or null if none.</summary>
    ErrorRecord? LastError { get; }

    /// <summary>Most recent errors (bounded, default 100).</summary>
    IReadOnlyList<ErrorRecord> RecentErrors { get; }

    /// <summary>Clear in-memory history. Does not affect persisted <c>errors.jsonl</c>.</summary>
    void Clear();

    event EventHandler<ErrorRecord>? ErrorWritten;
}
