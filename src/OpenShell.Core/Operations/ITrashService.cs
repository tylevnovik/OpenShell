using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// Trash service. Per ADR-0007, ADR-0020.
/// Implementations move items to <c>~/.openshell/trash/{timestamp}/...</c> and remember their original path
/// so they can be restored (undo delete) or pruned after a TTL.
/// </summary>
public interface ITrashService
{
    /// <summary>Move <paramref name="path"/> (resolving via its provider) into the trash.</summary>
    ValueTask<TrashEntry> MoveToTrashAsync(ItemPath path, CancellationToken cancellationToken = default);

    /// <summary>Restore a previously trashed entry by id.</summary>
    ValueTask RestoreAsync(Guid trashEntryId, CancellationToken cancellationToken = default);

    /// <summary>Permanently delete all trash entries older than <paramref name="ttl"/>.</summary>
    ValueTask PurgeAsync(TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>List current trash entries.</summary>
    ValueTask<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Empty the trash.</summary>
    ValueTask EmptyAsync(CancellationToken cancellationToken = default);
}

public sealed record TrashEntry
{
    public required Guid Id { get; init; }
    public required ItemPath OriginalPath { get; init; }
    /// <summary>Where the trashed content now lives, provider-relative.</summary>
    public required ItemPath TrashPath { get; init; }
    public required DateTimeOffset TrashedAt { get; init; }
    public long? SizeBytes { get; init; }
}
