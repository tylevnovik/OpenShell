using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// Per ADR-0007: optional fast-path for providers that can implement mutations natively
/// (file system rename, directory create, native delete). When a provider implements this,
/// the operation engine routes through it; otherwise the engine falls back to generic
/// content-stream transfer + trash.
/// </summary>
public interface IItemMutatorProvider
{
    ValueTask CreateDirectoryAsync(ItemPath path, CancellationToken cancellationToken = default);

    /// <summary>Delete the item. If <paramref name="recurse"/> is false and the path is a non-empty container, throw.</summary>
    ValueTask DeleteAsync(ItemPath path, bool recurse, CancellationToken cancellationToken = default);

    ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default);

    ValueTask SetTimestampsAsync(
        ItemPath path,
        DateTimeOffset? modified,
        DateTimeOffset? accessed,
        CancellationToken cancellationToken = default);
}
