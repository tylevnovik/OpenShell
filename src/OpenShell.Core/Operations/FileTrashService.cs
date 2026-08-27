using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// 基于 <c>~/.openshell/trash/{timestamp}/</c> 目录的 <see cref="ITrashService"/> 默认实现。Per ADR-0020 §4.
/// Trash 目录结构:
/// <code>
/// ~/.openshell/trash/
/// └── 2026-07-07T15-30-00-{guid8}/
///     ├── manifest.json   # 含 Id, OriginalPath, TrashedAt
///     └── file.txt        # 原 file/dir 内容
/// </code>
/// 默认仅支持 fs provider 路径 (直接用 <see cref="Directory"/>/<see cref="File"/> 移动);
/// 非 fs provider 抛 <see cref="NotSupportedException"/> (TODO: 跨 provider 抽取内容到 trash)。
/// Purge 默认 7 天自动清理 (ADR-0020 §4 约束)。
/// </summary>
public sealed class FileTrashService : ITrashService
{
    /// <summary>默认 Trash 保留期。Per ADR-0020 §4.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private readonly string _trashRoot;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>构造 FileTrashService。</summary>
    /// <param name="trashRoot">Trash 根目录, 默认 <see cref="OpenShell.OpenShellPaths.Trash"/>。</param>
    public FileTrashService(string? trashRoot = null)
    {
        _trashRoot = trashRoot ?? OpenShell.OpenShellPaths.Trash;
        _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
    }

    /// <inheritdoc />
    public async ValueTask<TrashEntry> MoveToTrashAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        // M5: 仅支持 fs provider; 非 fs 留 TODO。
        if (path.Provider != "fs")
        {
            throw new NotSupportedException(
                $"Trash currently only supports fs provider, got '{path.Provider}'. " +
                "TODO: implement cross-provider trash via IContentProvider extraction.");
        }

        var fsPath = ToFsPath(path);
        if (!File.Exists(fsPath) && !Directory.Exists(fsPath))
        {
            throw new FileNotFoundException($"Not found: {path.Display}", fsPath);
        }

        var id = Guid.NewGuid();
        var trashedAt = DateTimeOffset.UtcNow;
        var timestamp = trashedAt.UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ss");
        // 加 guid8 后缀避免同秒级 trash 目录冲突。
        var dirName = $"{timestamp}-{id.ToString("N")[..8]}";
        var trashDir = Path.Combine(_trashRoot, dirName);
        Directory.CreateDirectory(trashDir);

        var name = GetFileNameOrDirName(fsPath);
        var trashContentPath = Path.Combine(trashDir, name);

        // 移动文件或目录到 trash 目录。
        if (Directory.Exists(fsPath))
        {
            Directory.Move(fsPath, trashContentPath);
        }
        else
        {
            File.Move(fsPath, trashContentPath);
        }

        // 写 manifest.json (含 Id / OriginalPath / TrashedAt)。
        var manifest = new TrashManifest
        {
            Id = id,
            OriginalPath = path.Display,
            TrashedAt = trashedAt,
            Name = name,
        };
        var manifestPath = Path.Combine(trashDir, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(manifest, _jsonOptions);
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken).ConfigureAwait(false);

        // TODO: Unix 设置文件权限 0600 (chmod); Windows ACL 限当前用户 (ADR-0020 §10 约束, M5 简化为不处理)。
        SetUnixFilePermissions0600(trashDir);

        var trashItemPath = new ItemPath
        {
            Provider = "fs",
            InternalPath = trashContentPath.Replace('\\', '/'),
        };

        long? size = TryGetSize(trashContentPath);
        return new TrashEntry
        {
            Id = id,
            OriginalPath = path,
            TrashPath = trashItemPath,
            TrashedAt = trashedAt,
            SizeBytes = size,
        };
    }

    /// <inheritdoc />
    public async ValueTask RestoreAsync(Guid trashEntryId, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_trashRoot)) return;

        foreach (var dir in Directory.GetDirectories(_trashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            TrashManifest? manifest;
            try
            {
                var text = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize<TrashManifest>(text, _jsonOptions);
            }
            catch (JsonException) { continue; }
            catch (IOException) { continue; }

            if (manifest?.Id != trashEntryId) continue;

            // 找到 manifest 之外的内容文件/目录。
            var entries = Directory.GetFileSystemEntries(dir);
            string? contentPath = null;
            foreach (var e in entries)
            {
                if (!string.Equals(Path.GetFileName(e), "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    contentPath = e;
                    break;
                }
            }

            if (contentPath is null) return;

            var originalPath = ItemPath.Parse(manifest.OriginalPath);
            if (originalPath.Provider != "fs")
            {
                throw new NotSupportedException(
                    $"Restore only supports fs provider, got '{originalPath.Provider}'.");
            }

            var fsOriginal = ToFsPath(originalPath);
            var parent = Path.GetDirectoryName(fsOriginal);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // 移回原位置。
            if (Directory.Exists(contentPath))
            {
                Directory.Move(contentPath, fsOriginal);
            }
            else
            {
                File.Move(contentPath, fsOriginal);
            }

            // 清理空 trash 目录 (含 manifest.json)。
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* ignore */ }
            return;
        }
    }

    /// <inheritdoc />
    public ValueTask PurgeAsync(TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_trashRoot)) return ValueTask.CompletedTask;

        var cutoff = DateTimeOffset.UtcNow - ttl;

        foreach (var dir in Directory.GetDirectories(_trashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                // 无 manifest: 按目录修改时间判断。
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.LastWriteTimeUtc < cutoff.UtcDateTime)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (IOException) { /* ignore */ }
                continue;
            }

            TrashManifest? manifest;
            try
            {
                var text = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<TrashManifest>(text, _jsonOptions);
            }
            catch (JsonException) { continue; }
            catch (IOException) { continue; }

            if (manifest is null) continue;
            if (manifest.TrashedAt < cutoff)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* ignore */ }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TrashEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<TrashEntry>();
        if (!Directory.Exists(_trashRoot))
        {
            return ValueTask.FromResult<IReadOnlyList<TrashEntry>>(entries);
        }

        foreach (var dir in Directory.GetDirectories(_trashRoot))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            TrashManifest? manifest;
            try
            {
                var text = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<TrashManifest>(text, _jsonOptions);
            }
            catch (JsonException) { continue; }
            catch (IOException) { continue; }

            if (manifest is null) continue;

            var trashItemPath = new ItemPath
            {
                Provider = "fs",
                InternalPath = dir.Replace('\\', '/'),
            };
            entries.Add(new TrashEntry
            {
                Id = manifest.Id,
                OriginalPath = ItemPath.Parse(manifest.OriginalPath),
                TrashPath = trashItemPath,
                TrashedAt = manifest.TrashedAt,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<TrashEntry>>(entries);
    }

    /// <inheritdoc />
    public ValueTask EmptyAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_trashRoot)) return ValueTask.CompletedTask;

        foreach (var dir in Directory.GetDirectories(_trashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* ignore */ }
        }

        return ValueTask.CompletedTask;
    }

    // D-700: 与 FileSystemProvider 对齐，使用平台分隔符（内部路径统一为 '/'）。
    private static string ToFsPath(ItemPath path) => path.InternalPath.Replace('/', Path.DirectorySeparatorChar);

    private static string GetFileNameOrDirName(string fsPath)
    {
        var trimmed = fsPath.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? "item" : name;
    }

    private static long? TryGetSize(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                long sum = 0;
                foreach (var fi in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    sum += fi.Length;
                }
                return sum;
            }
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Unix 设置目录权限 0600 (仅 owner 可读写); Windows 留 TODO (ACL 处理简化)。</summary>
    private static void SetUnixFilePermissions0600(string path)
    {
        // Pre-existing fix: OperatingSystem.IsUnix() 不存在于 .NET BCL,
        // 等价语义为 !OperatingSystem.IsWindows() (Linux + macOS + BSD)。
        if (OperatingSystem.IsWindows()) return;
        try
        {
            // 0600 = owner read/write only.
            const int ownerReadWrite = 0x180;  // S_IRUSR | S_IWUSR
            System.IO.File.SetUnixFileMode(path, (UnixFileMode)ownerReadWrite);
        }
        catch
        {
            // 权限设置失败不阻断 trash 流程。
        }
    }

    /// <summary>Trash manifest 元数据, 持久化为 manifest.json。</summary>
    private sealed class TrashManifest
    {
        public Guid Id { get; set; }
        public string OriginalPath { get; set; } = string.Empty;
        public DateTimeOffset TrashedAt { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
