using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using IOPath = System.IO.Path;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Core.Tests.TestSupport;

/// <summary>
/// 最小文件系统 provider stub, 仅供 Core.Tests 单测使用。
/// 实现 <see cref="IContainerProvider"/> + <see cref="IContentProvider"/> 以便在不引用
/// OpenShell.Providers.FileSystem 的情况下测试搜索/预览/命令 (保持 Core.Tests 隔离)。
/// 用真实磁盘 (TempDir) 作后端, 支持 Recurse + Filter glob。
/// </summary>
internal sealed class StubFileProvider : IProvider, IContainerProvider, IContentProvider
{
    public ProviderInfo Info { get; } = new()
    {
        Name = "fs",
        Version = new Version(0, 1, 0),
        Description = "Stub file provider for unit tests",
        Author = "OpenShell.Core.Tests",
    };

    public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
    {
        ProviderCapability.Container,
        ProviderCapability.Content,
    };

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fsPath = ToFsPath(path);
        if (!System.IO.Directory.Exists(fsPath))
            yield break;

        DirectoryInfo dirInfo;
        IEnumerable<System.IO.FileSystemInfo> entries;
        try
        {
            dirInfo = new DirectoryInfo(fsPath);
            entries = dirInfo.EnumerateFileSystemInfos();
        }
        catch { yield break; }

        var hasFilter = !string.IsNullOrEmpty(options.Filter);
        foreach (var info in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = (info.Attributes & System.IO.FileAttributes.Directory) != 0;

            if (!isDirectory && hasFilter && !MatchesGlob(info.Name, options.Filter!))
                continue;

            var childPath = path.Combine(info.Name);

            if (!hasFilter || !isDirectory)
                yield return ToItem(info, childPath);

            if (options.Recurse && isDirectory)
            {
                await foreach (var sub in GetChildrenAsync(childPath, options, cancellationToken).ConfigureAwait(false))
                    yield return sub;
            }
        }
    }

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        var fsPath = ToFsPath(path);
        return ValueTask.FromResult<Stream>(new FileStream(
            fsPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192, useAsync: true));
    }

    private static string ToFsPath(ItemPath path) => path.InternalPath.Replace('/', IOPath.DirectorySeparatorChar);

    private static bool MatchesGlob(string name, string filter)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, name, ignoreCase: true);

    private static Item ToItem(System.IO.FileSystemInfo info, ItemPath path)
    {
        var isDirectory = (info.Attributes & System.IO.FileAttributes.Directory) != 0;
        var kind = isDirectory ? ItemKind.Directory : ItemKind.File;
        long? size = null;
        if (info is FileInfo f)
        {
            try { size = f.Length; } catch { }
        }
        return new Item
        {
            Path = path,
            Kind = kind,
            Size = size,
            Timestamps = new ItemTimestamps(info.CreationTimeUtc, info.LastWriteTimeUtc, info.LastAccessTimeUtc),
        };
    }
}
