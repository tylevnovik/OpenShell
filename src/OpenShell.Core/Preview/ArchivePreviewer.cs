using System.IO.Compression;
using System.Formats.Tar;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 归档预览器。Per ADR-0030 §2.
/// 支持格式: .zip (via <see cref="ZipArchive"/>), .tar.gz / .tgz / .tar (via <see cref="TarFile"/> / <see cref="TarReader"/>, .NET 8+)。
/// 列出包内前 100 个 entry (per ADR-0030 §2); 双击 entry 走 ADR-0017 路径访问 (本预览器不实现)。
/// </summary>
public sealed class ArchivePreviewer : IPreviewer
{
    private const int MaxEntries = 100;

    private static readonly HashSet<string> ZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jar", ".war", ".apk", ".ipa", ".oxps", ".xps",
    };
    private static readonly HashSet<string> TarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz",
    };

    private readonly Func<ItemPath, CancellationToken, Task<Stream>> _openRead;

    public ArchivePreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead)
    {
        _openRead = openRead;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        if (item.ContentType is { } ct)
        {
            if (ct.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase)) return true;
            if (ct.StartsWith("application/x-tar", StringComparison.OrdinalIgnoreCase)) return true;
            if (ct.StartsWith("application/gzip", StringComparison.OrdinalIgnoreCase)) return true;
        }
        var name = item.Path.GetName().ToLowerInvariant();
        return ZipExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
               || TarExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        var name = item.Path.GetName().ToLowerInvariant();
        Stream stream = await _openRead(item.Path, ct).ConfigureAwait(false);
        try
        {
            // tar.gz / .tgz / .tar.bz2 / .tar.xz 需要 GZipStream 解压后再交给 TarReader。
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false);
                stream = gzip; // ownership 转 gzip
                return await ReadTarEntriesAsync(gzip, item, ct).ConfigureAwait(false);
            }
            if (name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
            {
                // .NET 8 BCL 不含 BZip2 / XZ 解码器; 留待 M5+ 评估引入 SharpCompress 或类似库。
                return new PreviewViewModel.NotSupported(
                    "BZip2/XZ compressed tar archives require a third-party decompression library (not referenced).");
            }
            if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                return await ReadTarEntriesAsync(stream, item, ct).ConfigureAwait(false);
            }

            // 默认走 ZipArchive (.zip / .jar / .apk / .ipa / .oxps / .xps)。
            return ReadZipEntries(stream, item);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static PreviewViewModel ReadZipEntries(Stream stream, IItem item)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = new List<IItem>(capacity: Math.Min(zip.Entries.Count, MaxEntries));
            var added = 0;
            foreach (var entry in zip.Entries)
            {
                if (added >= MaxEntries) break;
                entries.Add(ToItem(item.Path, entry.FullName, entry.Length));
                added++;
            }
            return new PreviewViewModel.Archive(entries);
        }
        catch (Exception ex)
        {
            return new PreviewViewModel.NotSupported($"Failed to read zip archive: {ex.Message}");
        }
    }

    private static async ValueTask<PreviewViewModel> ReadTarEntriesAsync(Stream stream, IItem item, CancellationToken ct)
    {
        try
        {
            var entries = new List<IItem>(MaxEntries);
            // TarReader 需 seekable 流 (GZipStream 非 seekable), 先拷到 MemoryStream。
            Stream seekable;
            if (stream.CanSeek)
            {
                seekable = stream;
            }
            else
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                ms.Position = 0;
                seekable = ms;
            }

            using var tar = new TarReader(seekable);
            while (entries.Count < MaxEntries)
            {
                ct.ThrowIfCancellationRequested();
                var entry = await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false);
                if (entry is null) break;
                entries.Add(ToItem(item.Path, entry.Name, entry.Length));
            }
            return new PreviewViewModel.Archive(entries);
        }
        catch (Exception ex)
        {
            return new PreviewViewModel.NotSupported($"Failed to read tar archive: {ex.Message}");
        }
    }

    /// <summary>构造 entry 的 IItem (路径 = parent_archive::sub_path, Kind = File 或 Directory, 视尾部 / 而定)。</summary>
    private static IItem ToItem(ItemPath archivePath, string entryName, long size)
    {
        var isDir = entryName.EndsWith('/');
        return new Item
        {
            Path = new ItemPath { Provider = archivePath.Provider, InternalPath = $"{archivePath.InternalPath}/{entryName}" },
            Kind = isDir ? ItemKind.Directory : ItemKind.File,
            Size = isDir ? null : size,
        };
    }
}
