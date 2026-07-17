using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// Describes a mountable drive exposed by a <see cref="IDriveProvider"/>.
/// Renamed from <c>DriveInfo</c> to avoid collision with <see cref="System.IO.DriveInfo"/>.
/// </summary>
public sealed record ProviderDrive
{
    public required string Name { get; init; }             // "C:", "archive.zip", "s3:bucket"
    public required ItemPath Root { get; init; }           // "fs::C:/"
    public string? DisplayLabel { get; init; }            // "Local Disk (C:)"
    public long? TotalSize { get; init; }
    public long? FreeSpace { get; init; }
    public bool IsMounted { get; init; } = true;
}
