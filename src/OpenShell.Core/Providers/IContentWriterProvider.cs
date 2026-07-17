using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// Per ADR-0007: write content as a stream. Supplements ADR-0001.
/// Providers implement on demand: FileSystem and Archive (via ZipArchiveMode.Update) support writes;
/// Registry writes via <see cref="IPropertyWriterProvider"/> instead (values are properties, not byte streams).
/// </summary>
public interface IContentWriterProvider
{
    /// <summary>Open a writable stream at <paramref name="path"/>. Existing content is overwritten.</summary>
    ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default);

    /// <summary>Whether the path can be written to (e.g. exists and is not read-only).</summary>
    ValueTask<bool> CanWriteAsync(ItemPath path, CancellationToken cancellationToken = default);
}
