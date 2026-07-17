using System.IO.Compression;
using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using IOPath = System.IO.Path;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Archive;

/// <summary>
/// Zip archive provider. Per ADR-0017.
/// 路径格式：<c>zip::C:/path/to/archive.zip/inner/path</c>
/// 用 System.IO.Compression.ZipArchive（BCL 内置）实现流式访问，避免一次性解压到磁盘。
/// 支持 read + write：read 走 ZipArchiveMode.Read，write 走 ZipArchiveMode.Update（自动创建 zip 文件）。
/// </summary>
public sealed class ZipArchiveProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IContentWriterProvider,
    IItemMutatorProvider,
    IPropertyProvider
{
    private const string ArchiveExtension = ".zip";

    public ProviderInfo Info { get; } = new()
    {
        Name = "zip",
        Version = new Version(0, 1, 0),
        Description = "Zip archive provider (streaming read/write of entries)",
        Author = "OpenShell",
    };

    public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
    {
        ProviderCapability.Item,
        ProviderCapability.Container,
        ProviderCapability.Navigation,
        ProviderCapability.Content,
        ProviderCapability.ContentWrite,
        ProviderCapability.Property,
    };

    // ---- IItemProvider ----

    public ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        if (!File.Exists(archivePath))
            return ValueTask.FromResult<IItem?>(null);

        using var zip = ZipFile.OpenRead(archivePath);

        // 直接 entry 命中：文件或目录 entry（目录 entry 通常以 '/' 结尾）。
        var directEntry = zip.GetEntry(entryPath);
        if (directEntry is not null)
            return ValueTask.FromResult<IItem?>(ToItem(path, directEntry));

        // 当作目录处理：检查是否有 entry 的 FullName 以 entryPath + "/" 开头。
        var dirPrefix = NormalizeDirectoryEntry(entryPath);
        if (string.IsNullOrEmpty(dirPrefix))
        {
            // 根目录：archive 自身视为 Directory。
            return ValueTask.FromResult<IItem?>(new Item
            {
                Path = path,
                Kind = ItemKind.Directory,
                Timestamps = new ItemTimestamps(
                    File.GetCreationTimeUtc(archivePath),
                    File.GetLastWriteTimeUtc(archivePath),
                    File.GetLastAccessTimeUtc(archivePath)),
            });
        }

        var hasDescendant = false;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.StartsWith(dirPrefix, StringComparison.Ordinal))
            {
                hasDescendant = true;
                break;
            }
        }

        if (!hasDescendant)
            return ValueTask.FromResult<IItem?>(null);

        // 取目录下第一个 entry 的 LastWriteTime 作为目录时间戳（zip 目录 entry 通常无独立时间）。
        DateTimeOffset? dirModified = null;
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith(dirPrefix, StringComparison.Ordinal)) continue;
            dirModified = e.LastWriteTime;
            break;
        }

        return ValueTask.FromResult<IItem?>(new Item
        {
            Path = path,
            Kind = ItemKind.Directory,
            Timestamps = new ItemTimestamps(null, dirModified, null),
        });
    }

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (archivePath, entryPath) = SplitPath(path);
        if (!File.Exists(archivePath))
            yield break;

        using var zip = ZipFile.OpenRead(archivePath);
        var dirPrefix = NormalizeDirectoryEntry(entryPath);

        // 第一遍：枚举直接子项（去重），区分 file 与 subdirectory。
        // zip 内可能没有显式目录 entry（仅靠文件路径推断目录），所以采用路径前缀匹配。
        var directChildren = new Dictionary<string, ChildKind>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullName = entry.FullName;
            if (!fullName.StartsWith(dirPrefix, StringComparison.Ordinal))
                continue;

            var remaining = fullName.Substring(dirPrefix.Length);
            if (string.IsNullOrEmpty(remaining))
                continue;

            var sepIdx = remaining.IndexOf('/');
            if (sepIdx < 0)
            {
                // 直接文件子项。
                directChildren[remaining] = new ChildKind(false, entry);
            }
            else
            {
                // 子目录：取第一个 '/' 之前的部分作为目录名。
                var subDirName = remaining[..sepIdx];
                if (subDirName.Length == 0) continue;
                if (!directChildren.ContainsKey(subDirName))
                    directChildren[subDirName] = new ChildKind(true, null);
            }
        }

        foreach (var (name, kind) in directChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 隐藏过滤：以 '.' 开头视作隐藏（与 FS Provider 一致）。
            if (!options.IncludeHidden && name.StartsWith('.'))
                continue;

            var childPath = path.Combine(name);

            if (kind.IsDirectory)
            {
                // 目录直接 yield，filter 仅作用于文件。
                yield return new Item
                {
                    Path = childPath,
                    Kind = ItemKind.Directory,
                    Timestamps = kind.Entry is null ? ItemTimestamps.None
                        : new ItemTimestamps(null, kind.Entry.LastWriteTime, null),
                };

                if (options.Recurse && (options.MaxDepth < 0 || options.MaxDepth > 0))
                {
                    await foreach (var sub in GetChildrenAsync(
                        childPath,
                        options with { MaxDepth = options.MaxDepth - 1 },
                        cancellationToken).ConfigureAwait(false))
                    {
                        yield return sub;
                    }
                }
            }
            else
            {
                // 文件：应用 glob 过滤。
                if (!string.IsNullOrEmpty(options.Filter)
                    && !MatchesGlob(name, options.Filter!))
                {
                    continue;
                }

                yield return ToItem(childPath, kind.Entry!);
            }
        }
    }

    // ---- INavigationProvider ----

    public bool IsValidPath(ItemPath path)
    {
        if (path.Provider != "zip")
            return false;
        var (archivePath, _) = SplitPath(path);
        return archivePath.EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase)
            && archivePath.Length > ArchiveExtension.Length;
    }

    public ItemPath NormalizePath(ItemPath path)
    {
        var (archivePath, entryPath) = SplitPath(path);
        try
        {
            var fullArchive = IOPath.GetFullPath(archivePath).Replace('\\', '/');
            var normalizedEntry = entryPath.Trim('/');
            var newInternal = string.IsNullOrEmpty(normalizedEntry)
                ? fullArchive
                : $"{fullArchive}/{normalizedEntry}";
            return path with { InternalPath = newInternal };
        }
        catch (Exception) when (string.IsNullOrEmpty(archivePath))
        {
            return path;
        }
    }

    // ---- IContentProvider ----

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive not found: {archivePath}", archivePath);

        var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }

        ZipArchiveEntry? entry;
        try
        {
            entry = zip.GetEntry(entryPath);
        }
        catch
        {
            zip.Dispose();
            throw;
        }

        if (entry is null)
        {
            zip.Dispose();
            throw new FileNotFoundException($"Entry not found in archive: {entryPath}", archivePath);
        }

        Stream entryStream;
        try
        {
            entryStream = entry.Open();
        }
        catch
        {
            zip.Dispose();
            throw;
        }

        return ValueTask.FromResult<Stream>(new ZipEntryStream(entryStream, zip, fileStream));
    }

    // ---- IContentWriterProvider (ADR-0007) ----

    public ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        if (string.IsNullOrEmpty(entryPath))
            throw new ArgumentException("Cannot write to archive root; specify an entry path.", nameof(path));

        EnsureParentDirectory(archivePath);

        var fileExists = File.Exists(archivePath);
        var fileStream = new FileStream(
            archivePath,
            fileExists ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: false);
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }

        // 覆盖语义：若 entry 已存在则先删除再创建。
        var existing = zip.GetEntry(entryPath);
        existing?.Delete();

        ZipArchiveEntry newEntry;
        Stream entryStream;
        try
        {
            newEntry = zip.CreateEntry(entryPath);
            entryStream = newEntry.Open();
        }
        catch
        {
            zip.Dispose();
            throw;
        }

        return ValueTask.FromResult<Stream>(new ZipEntryStream(entryStream, zip, fileStream));
    }

    public ValueTask<bool> CanWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, _) = SplitPath(path);
        if (!File.Exists(archivePath))
        {
            var dir = IOPath.GetDirectoryName(archivePath);
            return ValueTask.FromResult(string.IsNullOrEmpty(dir) || Directory.Exists(dir));
        }
        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Write, FileShare.None);
            return ValueTask.FromResult(true);
        }
        catch
        {
            return ValueTask.FromResult(false);
        }
    }

    // ---- IItemMutatorProvider (ADR-0007) ----

    public ValueTask CreateDirectoryAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        EnsureArchiveExists(archivePath);

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: true);
        var dirEntry = NormalizeDirectoryEntry(entryPath);
        if (!string.IsNullOrEmpty(dirEntry) && zip.GetEntry(dirEntry) is null)
        {
            zip.CreateEntry(dirEntry);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(ItemPath path, bool recurse, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive not found: {archivePath}", archivePath);

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: true);

        // 先尝试当作文件 entry 删除。
        var entry = zip.GetEntry(entryPath);
        if (entry is not null)
        {
            entry.Delete();
            return ValueTask.CompletedTask;
        }

        // 再尝试当作目录：匹配 entryPath + "/" 前缀。
        var dirPrefix = NormalizeDirectoryEntry(entryPath);
        if (string.IsNullOrEmpty(dirPrefix))
            throw new InvalidOperationException("Cannot delete root of archive.");

        List<ZipArchiveEntry> matches;
        try
        {
            matches = zip.Entries
                .Where(e => e.FullName.StartsWith(dirPrefix, StringComparison.Ordinal))
                .ToList();
        }
        catch
        {
            throw;
        }

        if (matches.Count == 0)
            throw new FileNotFoundException($"Entry not found in archive: {entryPath}", archivePath);

        if (!recurse && matches.Count > 1)
            throw new InvalidOperationException(
                $"Directory '{entryPath}' is not empty. Use recurse=true to delete recursively.");

        foreach (var e in matches)
            e.Delete();

        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive not found: {archivePath}", archivePath);

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: true);

        var entry = zip.GetEntry(entryPath)
            ?? throw new FileNotFoundException($"Entry not found in archive: {entryPath}", archivePath);

        // ZipArchive 不支持原地 rename：通过 copy + delete 实现。
        var lastSep = entryPath.LastIndexOf('/');
        var parentPrefix = lastSep >= 0 ? entryPath[..(lastSep + 1)] : "";
        var newEntryPath = parentPrefix + newName;

        var newEntry = zip.CreateEntry(newEntryPath);
        using (var src = entry.Open())
        using (var dst = newEntry.Open())
        {
            src.CopyTo(dst);
        }
        newEntry.LastWriteTime = entry.LastWriteTime;
        entry.Delete();

        return ValueTask.CompletedTask;
    }

    public ValueTask SetTimestampsAsync(
        ItemPath path,
        DateTimeOffset? modified,
        DateTimeOffset? accessed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(path);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive not found: {archivePath}", archivePath);

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update, leaveOpen: true);

        var entry = zip.GetEntry(entryPath)
            ?? throw new FileNotFoundException($"Entry not found in archive: {entryPath}", archivePath);

        // zip 仅支持 LastWriteTime；accessed 字段在 zip 格式中无对应字段。
        if (modified.HasValue)
            entry.LastWriteTime = modified.Value;

        return ValueTask.CompletedTask;
    }

    // ---- IPropertyProvider ----

    public ValueTask<PropertyBag> GetPropertiesAsync(IItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (archivePath, entryPath) = SplitPath(item.Path);
        if (!File.Exists(archivePath))
            return ValueTask.FromResult(PropertyBag.Empty);

        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry(entryPath);
        if (entry is null)
            return ValueTask.FromResult(PropertyBag.Empty);

        var bag = PropertyBag.Empty
            .With("compressedLength", entry.CompressedLength)
            .With("uncompressedLength", entry.Length)
            .With("crc32", entry.Crc32.ToString("X8"))
            .With("lastWriteTime", entry.LastWriteTime)
            .With("archivePath", archivePath)
            .With("entryPath", entry.FullName)
            .With("externalAttributes", entry.ExternalAttributes.ToString("X8"))
            .With("isDirectory", entry.FullName.EndsWith('/'));
        return ValueTask.FromResult(bag);
    }

    // ---- Helpers ----

    /// <summary>
    /// 切分 ItemPath.InternalPath 为 archivePath（含 .zip 后缀）与 entryPath（相对包内路径）。
    /// 切分规则：第一个 .zip 出现的位置之后即为 entry 路径。
    /// 嵌套 zip（zip in zip）M4 不支持。
    /// </summary>
    private static (string archivePath, string entryPath) SplitPath(ItemPath path)
    {
        var internalPath = path.InternalPath.TrimStart('/');
        var idx = internalPath.IndexOf(ArchiveExtension, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return (internalPath, "");

        var afterZip = idx + ArchiveExtension.Length;
        var archivePath = internalPath[..afterZip];
        var entryPath = afterZip < internalPath.Length && internalPath[afterZip] == '/'
            ? internalPath[(afterZip + 1)..]
            : "";
        return (archivePath, entryPath);
    }

    /// <summary>规范化 entry path 为 zip 内部目录 entry 形式（末尾 '/'）。</summary>
    private static string NormalizeDirectoryEntry(string entryPath)
    {
        var trimmed = entryPath.Trim('/');
        return string.IsNullOrEmpty(trimmed) ? "" : trimmed + "/";
    }

    private static void EnsureParentDirectory(string archivePath)
    {
        var dir = IOPath.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>确保 zip 文件存在：不存在则创建空 archive。</summary>
    private static void EnsureArchiveExists(string archivePath)
    {
        if (File.Exists(archivePath))
            return;
        EnsureParentDirectory(archivePath);
        using var fs = new FileStream(archivePath, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
    }

    private static Item ToItem(ItemPath path, ZipArchiveEntry entry)
    {
        var isDirectory = entry.FullName.EndsWith('/');
        return new Item
        {
            Path = path,
            Kind = isDirectory ? ItemKind.Directory : ItemKind.File,
            Size = isDirectory ? null : entry.Length,
            Timestamps = new ItemTimestamps(null, entry.LastWriteTime, null),
        };
    }

    private static bool MatchesGlob(string name, string filter)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, name, ignoreCase: true);

    private readonly record struct ChildKind(bool IsDirectory, ZipArchiveEntry? Entry);
}

/// <summary>
/// Stream 包装器：在 dispose 时依次关闭 entry stream、ZipArchive、FileStream。
/// Per ADR-0017：entry stream 必须先关闭，再关闭 ZipArchive（写回 central directory），最后关闭 file。
/// </summary>
internal sealed class ZipEntryStream : Stream
{
    private readonly Stream _inner;
    private readonly ZipArchive _archive;
    private readonly FileStream _file;
    private bool _disposed;

    public ZipEntryStream(Stream inner, ZipArchive archive, FileStream file)
    {
        _inner = inner;
        _archive = archive;
        _file = file;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                // 顺序很重要：entry stream 必须先 dispose（写完未压缩缓冲）。
                _inner.Dispose();
                _archive.Dispose();
                _file.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
