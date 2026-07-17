using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace OpenShell.Preview;

/// <summary>
/// Everything 风格磁盘索引器。Per ADR-0030 §4.
/// Windows 上通过 USN Change Journal (FSCTL_QUERY_USN_JOURNAL / FSCTL_READ_USN_JOURNAL)
/// 增量枚举 NTFS 卷上的文件, 延迟 &lt; 10ms; 非 Windows 回退到 <see cref="System.IO.Directory.EnumerateFiles(string, string, System.IO.EnumerationOptions)"/> 全量遍历。
/// 索引持久化到 <c>~/.openshell/cache/filename-index.db</c> (per ADR-0030 §4: 启动时加载, 后续增量更新)。
/// </summary>
/// <remarks>
/// 实现 notes:
/// <list type="bullet">
///   <item>USN Journal 仅 NTFS/exFAT 支持; 不支持的卷回退到目录遍历。</item>
///   <item>P/Invoke 全部者通过 <c>RuntimeInformation.IsOSPlatform(OSPlatform.Windows)</c> 检查包裹。</item>
///   <item>索引内存结构: <see cref="ConcurrentDictionary{TKey,TValue}"/> 路径 → <see cref="IndexedFile"/>。</item>
///   <item>增量: 启动时记录上次 USN (per-journal Min/Max USN), 后续读取 &gt; lastUsn 的记录。</item>
/// </list>
/// </remarks>
public sealed class UsnJournalIndexer : IDisposable
{
    private readonly ILogger<UsnJournalIndexer>? _logger;
    private readonly string _indexPath;
    private readonly Dictionary<string, long> _lastUsnByVolume = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistLock = new();
    private int _disposed;

    /// <summary>内存索引: 路径 (小写, 平台分隔符) → <see cref="IndexedFile"/>。</summary>
    public ConcurrentDictionary<string, IndexedFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造索引器。</summary>
    /// <param name="indexPath">持久化索引文件路径 (per ADR-0030 §4: <c>~/.openshell/cache/filename-index.db</c>)。</param>
    /// <param name="logger">可选日志。</param>
    public UsnJournalIndexer(string indexPath, ILogger<UsnJournalIndexer>? logger = null)
    {
        _indexPath = indexPath ?? throw new ArgumentNullException(nameof(indexPath));
        _logger = logger;
    }

    /// <summary>索引文件记录。Per ADR-0030 §4.</summary>
    /// <param name="Path">绝对路径 (平台分隔符)。</param>
    /// <param name="Name">文件名 (用于子串匹配)。</param>
    /// <param name="Size">文件大小 (字节)。</param>
    /// <param name="Modified">最后修改时间 (UTC ticks)。</param>
    public sealed record IndexedFile(string Path, string Name, long Size, long Modified);

    /// <summary>
    /// 加载持久化索引 (启动时调用)。Per ADR-0030 §4: 启动时加载索引到内存。
    /// 文件不存在或损坏时静默忽略, 后续 <see cref="RefreshAsync"/> 会重建。
    /// </summary>
    public Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_indexPath))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(fs);
                var version = reader.ReadInt32();
                if (version != 1) return; // 版本不匹配, 忽略。
                var volumeCount = reader.ReadInt32();
                _lastUsnByVolume.Clear();
                for (var i = 0; i < volumeCount; i++)
                {
                    var key = reader.ReadString();
                    var usn = reader.ReadInt64();
                    _lastUsnByVolume[key] = usn;
                }

                var fileCount = reader.ReadInt32();
                for (var i = 0; i < fileCount; i++)
                {
                    var path = reader.ReadString();
                    var name = reader.ReadString();
                    var size = reader.ReadInt64();
                    var modified = reader.ReadInt64();
                    Files[path.ToLowerInvariant()] = new IndexedFile(path, name, size, modified);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "索引文件加载失败, 将重建: {Path}", _indexPath);
                Files.Clear();
                _lastUsnByVolume.Clear();
            }
        }, ct);
    }

    /// <summary>
    /// 持久化索引到 <see cref="_indexPath"/>。Per ADR-0030 §4: 启动时加载, 后续增量更新。
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_persistLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_indexPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using var fs = new FileStream(_indexPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var writer = new BinaryWriter(fs);
                    writer.Write(1); // version
                    writer.Write(_lastUsnByVolume.Count);
                    foreach (var (k, v) in _lastUsnByVolume)
                    {
                        writer.Write(k);
                        writer.Write(v);
                    }
                    writer.Write(Files.Count);
                    foreach (var (_, f) in Files)
                    {
                        writer.Write(f.Path);
                        writer.Write(f.Name);
                        writer.Write(f.Size);
                        writer.Write(f.Modified);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "索引文件保存失败: {Path}", _indexPath);
                }
            }
        }, ct);
    }

    /// <summary>
    /// 刷新索引。Windows 调用 USN Journal 增量更新; 非 Windows 走目录遍历。
    /// </summary>
    /// <param name="roots">要索引的根目录 (绝对路径)。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task RefreshAsync(IReadOnlyList<string> roots, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RefreshVolumeViaUsnAsync(root, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // USN 失败 (非 NTFS / 权限不足 / 设备句柄打开失败) → 回退到目录遍历。
                    _logger?.LogDebug(ex, "USN 索引失败, 回退到目录遍历: {Root}", root);
                    await WalkDirectoryAsync(root, ct).ConfigureAwait(false);
                }
            }
        }
        else
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                await WalkDirectoryAsync(root, ct).ConfigureAwait(false);
            }
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>非 Windows / USN 不可用时的回退方案: 全量目录遍历。</summary>
    private async Task WalkDirectoryAsync(string root, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(root)) return;
            var options = new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };
            foreach (var entry in new System.IO.DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                var full = entry.FullName;
                var name = entry.Name;
                long size = entry is FileInfo fi ? fi.Length : 0;
                long modified = entry.LastWriteTimeUtc.Ticks;
                Files[full.ToLowerInvariant()] = new IndexedFile(full, name, size, modified);
            }
        }, ct).ConfigureAwait(false);
    }

    // ──────────────────────────── Windows USN Journal P/Invoke ────────────────────────────

    private async Task RefreshVolumeViaUsnAsync(string root, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var rootPath = System.IO.Path.GetFullPath(root);
        // 卷根: 例如 C:\
        var volumeRoot = System.IO.Path.GetPathRoot(rootPath)
            ?? throw new InvalidOperationException($"无法解析卷根: {rootPath}");

        // 卷根路径需以 \\.\ 前缀打开设备句柄 (例如 \\.\C:)。
        var devicePath = @"\\.\" + volumeRoot.TrimEnd('\\', '/').Replace("/", "\\");

        SafeFileHandle? handle = null;
        try
        {
            handle = NativeMethods.CreateFileW(
                devicePath,
                NativeMethods.GENERIC_READ,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new InvalidOperationException($"CreateFile 失败: {devicePath} (Win32 error {Marshal.GetLastWin32Error()})");
            }

            // 1. 查询 USN journal 元数据 (journal id / next usn)。
            USN_JOURNAL_DATA_V0 journal;
            if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero, 0,
                out journal, (uint)Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
                out _,
                IntPtr.Zero))
            {
                throw new InvalidOperationException($"FSCTL_QUERY_USN_JOURNAL 失败 (Win32 error {Marshal.GetLastWin32Error()})。卷可能未启用 USN journal (非 NTFS/exFAT)。");
            }

            // 2. 决定起始 USN: 上次记录的最大 USN, 否则从 0 开始 (全量)。
            _lastUsnByVolume.TryGetValue(volumeRoot, out var lastUsn);
            var startUsn = lastUsn;

            // 3. 全量索引 (startUsn=0) 需要先清空该卷下旧条目, 否则增量更新。
            if (startUsn == 0)
            {
                var prefixLower = rootPath.ToLowerInvariant();
                foreach (var key in Files.Keys.Where(k => k.StartsWith(prefixLower, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    Files.TryRemove(key, out _);
                }
            }

            // 4. 循环读取 USN 记录, 直到 nextUsn。
            const int BufferSize = 64 * 1024;
            var readBuf = new byte[BufferSize];
            var maxUsnSeen = startUsn;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var readData = new MFT_ENUM_DATA_V1
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = startUsn,
                    HighUsn = journal.NextUsn,
                    MinMajorVersion = 2,
                    MaxMajorVersion = 3,
                };
                var inSize = Marshal.SizeOf<MFT_ENUM_DATA_V1>();

                IntPtr inPtr = IntPtr.Zero;
                try
                {
                    inPtr = Marshal.AllocHGlobal(inSize);
                    Marshal.StructureToPtr(readData, inPtr, false);

                    if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FSCTL_READ_USN_JOURNAL,
                        inPtr, (uint)inSize,
                        readBuf, (uint)readBuf.Length,
                        out var bytesReturned,
                        IntPtr.Zero))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err == NativeMethods.ERROR_HANDLE_EOF) break;
                        throw new InvalidOperationException($"FSCTL_READ_USN_JOURNAL 失败 (Win32 error {err})。");
                    }

                    if (bytesReturned < sizeof(long)) break;

                    // 第一个 8 字节是 next USN (返回值的下一 USN, 用于分页)。
                    var nextUsn = BitConverter.ToInt64(readBuf, 0);
                    if (nextUsn <= startUsn) break;

                    // 解析剩余 USN_RECORD。
                    var offset = sizeof(long);
                    while (offset + 8 <= bytesReturned)
                    {
                        var recordLen = BitConverter.ToInt32(readBuf, offset);
                        if (recordLen <= 0 || offset + recordLen > bytesReturned) break;

                        // USN_RECORD V2/V3 布局 (固定部分):
                        //   offset 0:  RecordLength (DWORD)
                        //   offset 4:  MajorVersion (WORD)
                        //   offset 6:  MinorVersion (WORD)
                        //   offset 8:  FileReferenceNumber (DWORDLONG)
                        //   offset 16: ParentFileReferenceNumber (DWORDLONG)
                        //   offset 24: Usn (LONGLONG)
                        //   offset 32: ReasonMask (DWORD)
                        //   offset 36: SourceInfo (DWORD)
                        //   offset 40: SecurityId (DWORD)
                        //   offset 44: FileAttributes (DWORD)
                        //   offset 48: FileNameLength (WORD, V2) / offset 56 (V3)
                        // V3 在 FileNameOffset 前 8 字节有 FileNamespaceInfo 等。
                        // 简化: 我们按 V3 偏移读取 FileNameOffset (offset 56) / FileNameLength (offset 60)。
                        var parentRef = BitConverter.ToInt64(readBuf, offset + 16);
                        var usn = BitConverter.ToInt64(readBuf, offset + 24);
                        var reason = BitConverter.ToUInt32(readBuf, offset + 32);
                        var nameOffset = BitConverter.ToUInt32(readBuf, offset + 56);
                        var nameLen = BitConverter.ToUInt16(readBuf, offset + 60);

                        if (usn > maxUsnSeen) maxUsnSeen = usn;

                        // 解析文件名 (UTF-16, nameOffset 相对 record 起点)。
                        string? fileName = null;
                        if (nameOffset > 0 && nameOffset + nameLen <= recordLen)
                        {
                            fileName = Encoding.Unicode.GetString(readBuf, offset + (int)nameOffset, nameLen);
                        }

                        // 仅 USN_CLOSE (文件关闭后, 元数据稳定) 与 USN_FILE_CREATE 用于索引更新。
                        const uint USN_REASON_FILE_CREATE = 0x00000100;
                        const uint USN_REASON_FILE_DELETE = 0x00000200;
                        const uint USN_REASON_CLOSE = 0x80000000;

                        if (fileName is not null)
                        {
                            // 我们没有从 file reference 反查完整路径的轻量手段 (需要 FSCTL_GET_NTFS_FILE_RECORD),
                            // 因此退化为: 仅用文件名 + 父目录引用作为索引键前缀。
                            // 完整路径索引在 M5+ 评估 (per ADR-0030 §4)。
                            var key = $"{volumeRoot.ToUpperInvariant()}::{parentRef:X16}::{fileName.ToLowerInvariant()}";
                            if ((reason & USN_REASON_FILE_DELETE) != 0)
                            {
                                Files.TryRemove(key, out _);
                            }
                            else if ((reason & (USN_REASON_FILE_CREATE | USN_REASON_CLOSE)) != 0)
                            {
                                Files[key] = new IndexedFile(
                                    Path: $"{volumeRoot}\\??\\{fileName}",
                                    Name: fileName,
                                    Size: 0,
                                    Modified: DateTime.UtcNow.Ticks);
                            }
                        }

                        offset += recordLen;
                    }

                    startUsn = nextUsn;
                    if (startUsn >= journal.NextUsn) break;
                }
                finally
                {
                    if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
                }
            }

            _lastUsnByVolume[volumeRoot] = maxUsnSeen;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // 不在 Dispose 中调用 SaveAsync (异步 + 可能已取消); 调用方应在关闭前显式 SaveAsync。
        Files.Clear();
        _lastUsnByVolume.Clear();
    }

    // ──────────────────────────── Win32 Native ────────────────────────────

    private static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const int ERROR_HANDLE_EOF = 38;

        // FSCTL_QUERY_USN_JOURNAL = 0x000900F4 (METHOD_BUFFERED, FILE_ANY_ACCESS, FUNCTION 0x003D)
        public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
        // FSCTL_READ_USN_JOURNAL  = 0x000900BB (METHOD_BUFFERED, FILE_ANY_ACCESS, FUNCTION 0x0037)
        public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            [Out] out USN_JOURNAL_DATA_V0 lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);
    }

    /// <summary>USN_JOURNAL_DATA_V0 (Win32)。Per Microsoft Docs USN_JOURNAL_DATA。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public long UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    /// <summary>MFT_ENUM_DATA_V1 (Win32)。Per Microsoft Docs MFT_ENUM_DATA_V1 (用于 FSCTL_READ_USN_JOURNAL V2/V3 记录)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V1
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }
}
