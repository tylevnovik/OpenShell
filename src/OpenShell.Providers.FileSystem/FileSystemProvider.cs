using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using IOPath = System.IO.Path;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.FileSystem;

/// <summary>
/// FileSystem provider. Per ADR-0001, declares Item + Container + Navigation + Content + Drive capabilities.
/// Per ADR-0002, all APIs are async + cancellable + streaming.
/// </summary>
public sealed class FileSystemProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IContentWriterProvider,
    IPropertyProvider,
    IItemMutatorProvider,
    IDriveProvider
{
    public ProviderInfo Info { get; } = new()
    {
        Name = "fs",
        Version = new Version(0, 1, 0),
        Description = "Local filesystem provider",
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
        ProviderCapability.Drive,
    };

    public ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        FileSystemInfo? info = Directory.Exists(fsPath)
            ? new DirectoryInfo(fsPath)
            : System.IO.File.Exists(fsPath)
                ? new FileInfo(fsPath)
                : null;
        return ValueTask.FromResult(info is null ? null : (IItem?)ToItem(info, path));
    }

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        if (!Directory.Exists(fsPath))
            yield break;

        var dirInfo = new DirectoryInfo(fsPath);
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = dirInfo.EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }

        foreach (var info in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.IncludeHidden && IsHidden(info)) continue;
            if (!options.IncludeSystem && IsSystem(info)) continue;

            var isDirectory = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
            var hasFilter = !string.IsNullOrEmpty(options.Filter);

            // Filter applies to files only; directories must pass through so recursion can happen.
            if (!isDirectory && hasFilter && !MatchesGlob(info.Name, options.Filter!)) continue;

            var childPath = path.Combine(info.Name);

            // When filtering, do not yield directories themselves — only their matching descendants.
            if (!hasFilter || !isDirectory)
            {
                yield return ToItem(info, childPath);
            }

            if (options.Recurse && isDirectory && (options.MaxDepth < 0 || options.MaxDepth > 0))
            {
                await foreach (var sub in GetChildrenAsync(childPath, options, cancellationToken).ConfigureAwait(false))
                    yield return sub;
            }
        }
    }

    public bool IsValidPath(ItemPath path) => path.Provider == "fs" && path.InternalPath.Length > 0;

    public ItemPath NormalizePath(ItemPath path)
    {
        var fsPath = ToFsPath(path);
        var full = IOPath.GetFullPath(fsPath);
        return path with { InternalPath = full.Replace('\\', '/') };
    }

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        return ValueTask.FromResult<Stream>(new FileStream(fsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true));
    }

    // ---- IContentWriterProvider (ADR-0007) ----

    public ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        var dir = IOPath.GetDirectoryName(fsPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return ValueTask.FromResult<Stream>(new FileStream(fsPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true));
    }

    public ValueTask<bool> CanWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        if (System.IO.File.Exists(fsPath))
        {
            try
            {
                using var fs = new FileStream(fsPath, FileMode.Open, FileAccess.Write, FileShare.None);
                return ValueTask.FromResult(true);
            }
            catch { return ValueTask.FromResult(false); }
        }
        var dir = IOPath.GetDirectoryName(fsPath);
        if (!string.IsNullOrEmpty(dir)) return ValueTask.FromResult(Directory.Exists(dir));
        return ValueTask.FromResult(false);
    }

    // ---- IItemMutatorProvider (ADR-0007) ----

    public ValueTask CreateDirectoryAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        Directory.CreateDirectory(fsPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(ItemPath path, bool recurse, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        if (Directory.Exists(fsPath))
        {
            Directory.Delete(fsPath, recursive: recurse);
        }
        else if (System.IO.File.Exists(fsPath))
        {
            System.IO.File.Delete(fsPath);
        }
        else
        {
            throw new FileNotFoundException($"Not found: {path.Display}", fsPath);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        var newFsPath = IOPath.Combine(IOPath.GetDirectoryName(fsPath) ?? string.Empty, newName);

        if (Directory.Exists(fsPath))
            Directory.Move(fsPath, newFsPath);
        else if (System.IO.File.Exists(fsPath))
            System.IO.File.Move(fsPath, newFsPath);
        else
            throw new FileNotFoundException($"Not found: {path.Display}", fsPath);

        return ValueTask.CompletedTask;
    }

    public ValueTask SetTimestampsAsync(
        ItemPath path,
        DateTimeOffset? modified,
        DateTimeOffset? accessed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(path);
        if (Directory.Exists(fsPath))
        {
            var d = new DirectoryInfo(fsPath);
            if (modified.HasValue) d.LastWriteTimeUtc = modified.Value.UtcDateTime;
            if (accessed.HasValue) d.LastAccessTimeUtc = accessed.Value.UtcDateTime;
        }
        else if (System.IO.File.Exists(fsPath))
        {
            var f = new FileInfo(fsPath);
            if (modified.HasValue) f.LastWriteTimeUtc = modified.Value.UtcDateTime;
            if (accessed.HasValue) f.LastAccessTimeUtc = accessed.Value.UtcDateTime;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<PropertyBag> GetPropertiesAsync(IItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fsPath = ToFsPath(item.Path);
        FileSystemInfo? info = Directory.Exists(fsPath)
            ? new DirectoryInfo(fsPath)
            : System.IO.File.Exists(fsPath)
                ? (FileSystemInfo)new FileInfo(fsPath)
                : null;
        if (info is null)
            return ValueTask.FromResult(PropertyBag.Empty);

        var bag = PropertyBag.Empty
            .With("attributes", info.Attributes.ToString())
            .With("extension", info.Extension)
            .With("readonly", (info.Attributes & FileAttributes.ReadOnly) != 0);
        return ValueTask.FromResult(bag);
    }

    public async ValueTask<IReadOnlyList<ProviderDrive>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drives = await Task.Run(() => System.IO.DriveInfo.GetDrives(), cancellationToken).ConfigureAwait(false);
        var result = new List<ProviderDrive>(drives.Length);
        foreach (var d in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!d.IsReady) continue;
            result.Add(new ProviderDrive
            {
                Name = d.Name.TrimEnd('\\'),
                Root = new ItemPath { Provider = "fs", InternalPath = d.Name.TrimEnd('\\') + "/" },
                DisplayLabel = d.VolumeLabel.Length > 0 ? $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})" : d.Name,
                TotalSize = d.TotalSize,
                FreeSpace = d.AvailableFreeSpace,
            });
        }
        return result;
    }

    private static string ToFsPath(ItemPath path) => path.InternalPath.Replace('/', '\\');

    private static Item ToItem(FileSystemInfo info, ItemPath path)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
        var kind = isDirectory ? ItemKind.Directory : ItemKind.File;
        long? size = null;
        if (info is FileInfo f)
        {
            try { size = f.Length; } catch { /* may not exist anymore */ }
        }
        return new Item
        {
            Path = path,
            Kind = kind,
            Size = size,
            Timestamps = new ItemTimestamps(
                info.CreationTimeUtc,
                info.LastWriteTimeUtc,
                info.LastAccessTimeUtc),
        };
    }

    private static bool IsHidden(FileSystemInfo info)
        => info.Attributes.HasFlag(FileAttributes.Hidden);

    private static bool IsSystem(FileSystemInfo info)
        => info.Attributes.HasFlag(FileAttributes.System);

    private static bool MatchesGlob(string name, string filter)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, name, ignoreCase: true);
}
