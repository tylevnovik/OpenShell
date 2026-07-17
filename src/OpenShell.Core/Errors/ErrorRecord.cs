using System.Text.Json.Serialization;
using OpenShell.Paths;

namespace OpenShell.Errors;

/// <summary>
/// Immutable structured error record. Per ADR-0026.
/// All command exceptions must be converted to <see cref="ErrorRecord"/> before reaching the host.
/// </summary>
public sealed record ErrorRecord
{
    public ErrorCategory Category { get; init; } = ErrorCategory.Unknown;

    /// <summary>One-line error message.</summary>
    public required string Message { get; init; }

    /// <summary>Optional multi-line detail (stack trace, inner details, ...).</summary>
    public string? Detail { get; init; }

    /// <summary>Path involved in the error, if applicable.</summary>
    public ItemPath? TargetPath { get; init; }

    /// <summary>Name of the failing command (e.g. "copy-item").</summary>
    public string? Operation { get; init; }

    public ErrorPhase Phase { get; init; } = ErrorPhase.Unknown;

    /// <summary>Original exception, if any. Not serialised.</summary>
    [JsonIgnore]
    public Exception? Exception { get; init; }

    /// <summary>Actionable suggestion. Must contain a concrete command or step — never "see docs".</summary>
    public string? Suggestion { get; init; }

    /// <summary>Unique id for reference in scripts and logs.</summary>
    public Guid ErrorId { get; init; } = Guid.NewGuid();

    /// <summary>Wall-clock time the error was raised.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Builds an <see cref="ErrorRecord"/> from an exception, mapping common types.</summary>
    public static ErrorRecord FromException(
        Exception ex,
        string? operation = null,
        ItemPath? targetPath = null,
        ErrorPhase phase = ErrorPhase.Unknown,
        string? suggestion = null)
    {
        var category = ex switch
        {
            OpenShellException ose => ose.Category,
            ArgumentException => ErrorCategory.InvalidArgument,
            OperationCanceledException => ErrorCategory.OperationCancelled,
            TimeoutException => ErrorCategory.OperationTimeout,
            UnauthorizedAccessException => ErrorCategory.PermissionDenied,
            FileNotFoundException => ErrorCategory.ItemNotFound,
            DirectoryNotFoundException => ErrorCategory.ItemNotFound,
            IOException => ErrorCategory.IOError,
            OutOfMemoryException => ErrorCategory.OutOfMemory,
            _ => ErrorCategory.Unknown,
        };

        return new ErrorRecord
        {
            Category = category,
            Message = ex.Message,
            Detail = ex.ToString(),
            Operation = operation,
            TargetPath = targetPath,
            Phase = phase,
            Exception = ex,
            Suggestion = suggestion ?? SuggestFor(category),
        };
    }

    private static string? SuggestFor(ErrorCategory category) => category switch
    {
        ErrorCategory.PermissionDenied => "retry with elevated privileges (Run as Administrator)",
        ErrorCategory.ItemNotFound => "check path or use get-childitem to enumerate",
        ErrorCategory.ProviderNotFound => "register provider via 'install-provider <name>'",
        ErrorCategory.CapabilityNotSupported => "this provider does not support the requested capability; run 'get-help about_providers'",
        ErrorCategory.CircuitBroken => "remote circuit is open; wait 30s or check 'get-remote-config'",
        ErrorCategory.AuthenticationFailed => "run 'set-credential <account>' to refresh",
        _ => null,
    };

    public override string ToString()
    {
        var sp = TargetPath is { } p ? $"  path: {p.Display}\n" : "";
        var sop = Operation is { } op ? $"  command: {op}\n" : "";
        var sph = Phase is not ErrorPhase.Unknown ? $"  phase: {Phase.ToString().ToLowerInvariant()}\n" : "";
        var ssg = Suggestion is { } sg ? $"  suggestion: {sg}\n" : "";
        var sid = $"  error-id: {ErrorId}\n";
        return $"[error] {Operation ?? "unknown"}: {Message}\n{sp}{sop}{sph}{ssg}{sid}".TrimEnd('\n');
    }
}
