namespace OpenShell.Operations;

/// <summary>
/// Common options for copy/move operations. Per ADR-0007.
/// </summary>
public sealed record CopyOptions
{
    /// <summary>Recurse into subdirectories. Default true for directories.</summary>
    public bool Recurse { get; init; } = true;

    /// <summary>Overwrite existing destination. Default false.</summary>
    public bool Force { get; init; } = false;

    /// <summary>Stop on first error instead of continuing. Default false (collect errors).</summary>
    public bool StopOnError { get; init; } = false;

    /// <summary>Roll back completed items when cancelled. Default false.</summary>
    public bool RollbackOnCancel { get; init; } = false;

    /// <summary>Buffer size for stream copy. Default 64KB.</summary>
    public int BufferSize { get; init; } = 64 * 1024;
}

/// <summary>
/// Move options. Per ADR-0007.
/// </summary>
public sealed record MoveOptions
{
    public bool Force { get; init; } = false;
    public bool StopOnError { get; init; } = false;
    public bool RollbackOnCancel { get; init; } = true;   // move is destructive, default rollback
    public int BufferSize { get; init; } = 64 * 1024;
}

/// <summary>
/// Delete options. Per ADR-0007.
/// </summary>
public sealed record DeleteOptions
{
    /// <summary>Recurse into directories. Default true.</summary>
    public bool Recurse { get; init; } = true;

    /// <summary>Send to trash (default) or physically delete. <c>--force</c> sets false.</summary>
    public bool UseTrash { get; init; } = true;

    public bool StopOnError { get; init; } = false;
}

/// <summary>Touch options. Per ADR-0007.</summary>
public sealed record TouchOptions
{
    /// <summary>If true, create the file when missing. Default true.</summary>
    public bool CreateIfMissing { get; init; } = true;
    /// <summary>If null, update both access and modified time to now.</summary>
    public DateTimeOffset? Time { get; init; } = null;
}

/// <summary>Create directory options.</summary>
public sealed record CreateDirectoryOptions
{
    /// <summary>Create intermediate directories if missing. Default true.</summary>
    public bool CreateIntermediate { get; init; } = true;
}
