using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Remote;

/// <summary>
/// SFTP Provider (ADR-0019)。基于 SSH.NET 的 <see cref="SftpClient"/> 实现。
/// 路径格式: <c>sftp::user@host[:port]/path/to/file</c>。
/// 支持 Item / Container / Navigation / Content / ContentWrite / Property 能力。
/// 不实现 <see cref="IItemMutatorProvider"/>: SFTP 原子 rename 到任意位置语义复杂, M4 阶段先不加。
/// </summary>
/// <remarks>
/// 实现 ADR-0019 §9 §10 与 ADR-0001 §1:
/// <list type="bullet">
///   <item>连接池: 按 host+user+port 缓存 <see cref="SftpClient"/>, 同 key 操作串行化 (SemaphoreSlim)。</item>
///   <item>SSH.NET 大部分方法是同步的, 用 <see cref="Task.Run{TResult}(Func{TResult})"/> 包装为异步。</item>
///   <item>连接失败 / 鉴权失败抛 <see cref="SftpProviderException"/> (包装为 OpenShellException)。</item>
///   <item>路径不存在 (GetItem / GetChildren) 返回 null / 空枚举, 不抛异常。</item>
/// </list>
/// </remarks>
public sealed class SftpProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IContentWriterProvider,
    IPropertyProvider,
    IDisposable
{
    private readonly SftpConnectionPool _pool;

    public SftpProvider(ICredentialProvider credProvider)
    {
        ArgumentNullException.ThrowIfNull(credProvider);
        _pool = new SftpConnectionPool(credProvider);
    }

    public ProviderInfo Info { get; } = new()
    {
        Name = "sftp",
        Version = new Version(0, 1, 0),
        Description = "SFTP remote provider (SSH.NET)",
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

    /// <summary>Per ADR-0001 §1: read a single item at <paramref name="path"/>. Path 不存在返回 null。</summary>
    public async ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (user, host, port, remotePath) = ParseInternalPath(path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。
        try
        {
            var attrs = await _pool.ExecuteAsync(
                user, host, port,
                client => client.GetAttributes(rp),
                cancellationToken).ConfigureAwait(false);
            return ToItem(path, attrs);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (SftpProviderException)
        {
            throw;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    // ---- IContainerProvider ----

    /// <summary>Per ADR-0002: streaming + cancellable enumeration。</summary>
    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (user, host, port, remotePath) = ParseInternalPath(path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。

        // 一次性拉取目录列表 (SSH.NET ListDirectory 是同步阻塞调用)。
        // 在持有连接 semaphore 期间完成列举, 释放后再 yield, 避免长流式枚举期间阻塞其他连接操作。
        List<ISftpFile> entries;
        try
        {
            entries = await _pool.ExecuteAsync(
                user, host, port,
                client => client.ListDirectory(rp).ToList(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            yield break;
        }
        catch (SftpProviderException)
        {
            throw;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // SFTP 服务器返回的目录列举包含 "." 和 "..", 需跳过。
            if (entry.Name is "." or "..")
                continue;

            // Hidden: Unix 隐藏文件以 '.' 开头 (SFTP 协议本身无 hidden 标志位)。
            if (!options.IncludeHidden && entry.Name.StartsWith('.'))
                continue;

            var isDirectory = entry.Attributes.IsDirectory;
            var hasFilter = !string.IsNullOrEmpty(options.Filter);

            // Filter 仅作用于文件; 目录需要穿透以便递归。
            if (!isDirectory && hasFilter && !MatchesGlob(entry.Name, options.Filter!))
                continue;

            var childPath = path.Combine(entry.Name);

            // 有 filter 时不单独 yield 目录本身 (与 FileSystemProvider 语义一致)。
            if (!hasFilter || !isDirectory)
            {
                yield return ToItem(childPath, entry.Attributes);
            }

            if (options.Recurse && isDirectory && (options.MaxDepth < 0 || options.MaxDepth > 0))
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
    }

    // ---- INavigationProvider ----

    public bool IsValidPath(ItemPath path)
    {
        if (path.Provider != "sftp")
            return false;
        return TryParseInternalPath(path.InternalPath, out _);
    }

    public ItemPath NormalizePath(ItemPath path)
    {
        var (user, host, port, remotePath) = ParseInternalPath(path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        var normalized = $"{user}@{host}:{port}{rp}";
        return path with { InternalPath = normalized };
    }

    // ---- IContentProvider ----

    public async ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (user, host, port, remotePath) = ParseInternalPath(path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。
        try
        {
            // SftpClient.OpenRead 返回的 SftpFileStream 在 dispose 时仅关闭远程 file handle,
            // 不影响 SftpClient 连接本身。连接由 _pool 管理, 此处 stream 可独立使用。
            return await _pool.ExecuteAsync(
                user, host, port,
                client => client.OpenRead(rp),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new ItemNotFoundException($"SFTP item not found: {path.Display}", ex);
        }
    }

    // ---- IContentWriterProvider ----

    public async ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (user, host, port, remotePath) = ParseInternalPath(path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。
        try
        {
            // OpenWrite 语义: 存在则覆盖, 不存在则创建 (与 FileSystemProvider.OpenWrite 一致)。
            // SSH.NET 2025.x 没有 CreateDirectoriesIfNotExist, 需逐级创建父目录。
            var parent = GetParentRemote(rp);
            if (!string.IsNullOrEmpty(parent) && parent != "/")
            {
                await _pool.ExecuteAsync(
                    user, host, port,
                    client => EnsureRemoteDirectory(client, parent),
                    cancellationToken).ConfigureAwait(false);
            }

            return await _pool.ExecuteAsync(
                user, host, port,
                client => client.OpenWrite(rp),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new PermissionDeniedException($"SFTP write denied: {path.Display}", ex);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new ItemNotFoundException($"SFTP parent directory not found: {path.Display}", ex);
        }
    }

    public async ValueTask<bool> CanWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // SFTP 无法预先准确判断写权限, 这里用启发式:
        // 1) 连接 + 鉴权成功 → 至少有登录权限
        // 2) 路径的父目录存在 → 通常可写
        // 真实写权限只能在写操作时由服务器返回 SftpPermissionDeniedException 才能确定。
        var (user, host, port, remotePath) = ParseInternalPath(path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。
        var parent = GetParentRemote(rp);
        try
        {
            var parentAttrs = await _pool.ExecuteAsync(
                user, host, port,
                client => string.IsNullOrEmpty(parent) ? null : (SftpFileAttributes?)client.GetAttributes(parent),
                cancellationToken).ConfigureAwait(false);
            return parentAttrs is null || parentAttrs.IsDirectory;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    // ---- ADR-0034 §6: 分块上传 (multipart/chunked upload detection) ----

    /// <summary>大文件分块上传阈值: 100MB。Per ADR-0034 §6. 超过此阈值时使用显式分块写入。</summary>
    public const long ChunkedUploadThreshold = 100L * 1024 * 1024;

    /// <summary>分块写入的缓冲区大小: 4MB。Per ADR-0034 §6 (SFTP: stream in chunks)。</summary>
    private const int ChunkBufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// 上传文件到 SFTP, 支持大文件分块写入。Per ADR-0034 §6.
    /// 若源流长度可探测且超过 <see cref="ChunkedUploadThreshold"/> (100MB), 使用显式分块写入
    /// (stream in chunks), 避免单次上传占用过多内存。否则直接通过 <see cref="OpenWriteAsync"/> 流式写入。
    /// </summary>
    /// <param name="source">源数据流。</param>
    /// <param name="dest">目标路径。</param>
    /// <param name="progress">可选进度回调 (已传输字节数)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UploadFileAsync(
        Stream source,
        ItemPath dest,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        var (user, host, port, remotePath) = ParseInternalPath(dest.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。

        // 确保父目录存在。
        var parent = GetParentRemote(rp);
        if (!string.IsNullOrEmpty(parent) && parent != "/")
        {
            await _pool.ExecuteAsync(
                user, host, port,
                client => EnsureRemoteDirectory(client, parent),
                cancellationToken).ConfigureAwait(false);
        }

        // 检测是否需要分块上传: 源流可探测长度且超过阈值。
        var useChunked = source.CanSeek && source.Length > ChunkedUploadThreshold;
        long totalWritten = 0;

        // 获取 SFTP 写入流 (连接在 ExecuteAsync 返回后释放, SftpFileStream 独立使用)。
        Stream destStream;
        try
        {
            destStream = await _pool.ExecuteAsync(
                user, host, port,
                client => client.OpenWrite(rp),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new PermissionDeniedException($"SFTP write denied: {dest.Display}", ex);
        }

        await using (destStream)
        {
            var buffer = new byte[ChunkBufferSize];
            int read;

            while ((read = await source.ReadAsync(buffer.AsMemory(0, ChunkBufferSize), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                totalWritten += read;
                progress?.Report(totalWritten);
            }

            await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // 分块上传模式下, 记录信息日志 (非分块模式静默)。
        if (useChunked)
        {
            Console.Error.WriteLine(
                $"[sftp] chunked upload completed: {dest.Display} ({totalWritten} bytes, chunk={ChunkBufferSize})");
        }
    }

    // ---- IPropertyProvider ----

    public async ValueTask<PropertyBag> GetPropertiesAsync(IItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (user, host, port, remotePath) = ParseInternalPath(item.Path.InternalPath);
        var rp = NormalizeRemotePath(remotePath);
        RemotePathValidator.Validate(rp); // ADR-0034 §6: 路径安全校验。
        try
        {
            var attrs = await _pool.ExecuteAsync(
                user, host, port,
                client => client.GetAttributes(rp),
                cancellationToken).ConfigureAwait(false);

            return PropertyBag.Empty
                .With("size", attrs.Size)
                .With("userId", attrs.UserId)
                .With("groupId", attrs.GroupId)
                .With("isDirectory", attrs.IsDirectory)
                .With("isRegularFile", attrs.IsRegularFile)
                .With("isSymbolicLink", attrs.IsSymbolicLink)
                .With("isBlockDevice", attrs.IsBlockDevice)
                .With("isCharacterDevice", attrs.IsCharacterDevice)
                .With("isNamedPipe", attrs.IsNamedPipe)
                .With("isSocket", attrs.IsSocket)
                .With("ownerCanRead", attrs.OwnerCanRead)
                .With("ownerCanWrite", attrs.OwnerCanWrite)
                .With("ownerCanExecute", attrs.OwnerCanExecute)
                .With("groupCanRead", attrs.GroupCanRead)
                .With("groupCanWrite", attrs.GroupCanWrite)
                .With("groupCanExecute", attrs.GroupCanExecute)
                .With("othersCanRead", attrs.OthersCanRead)
                .With("othersCanWrite", attrs.OthersCanWrite)
                .With("othersCanExecute", attrs.OthersCanExecute)
                .With("lastWriteTime", attrs.LastWriteTime)
                .With("lastAccessTime", attrs.LastAccessTime)
                .With("host", host)
                .With("port", port)
                .With("user", user);
        }
        catch (SftpPathNotFoundException)
        {
            return PropertyBag.Empty;
        }
        catch (SftpProviderException)
        {
            throw;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return PropertyBag.Empty;
        }
    }

    /// <summary>
    /// Test connection: 通过执行一次轻量 SFTP 操作 (ListDirectory "/") 验证连接 + 鉴权 + 文件系统可用性。
    /// Per ADR-0019: 失败返回 false, 不抛异常, 由命令层向用户报告错误。
    /// </summary>
    public async ValueTask<bool> TestConnectionAsync(string user, string host, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _pool.ExecuteAsync(
                user, host, port,
                client =>
                {
                    // 触发 ListDirectory 以验证文件系统访问权限 (不仅鉴权)。
                    var _ = client.ListDirectory("/").FirstOrDefault();
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _pool.Dispose();

    /// <summary>IH-006: 测试入口——强制断开所有池化连接, 下一次操作触发重连。</summary>
    internal void DisconnectPooledConnections() => _pool.DisconnectAll();

    // ---- 路径解析 helpers ----

    /// <summary>
    /// 解析 SFTP 内部路径 <c>user@host[:port]/path/to/file</c>。
    /// Per ADR-0019 §2: account = <c>user@host:port</c>, key = <c>/path/to/file</c>。
    /// </summary>
    /// <exception cref="ArgumentException">路径格式无效 (缺少 user@ 前缀)。</exception>
    internal static (string user, string host, int port, string remotePath) ParseInternalPath(string internalPath)
    {
        if (!TryParseInternalPath(internalPath, out var parsed))
            throw new ArgumentException(
                $"Invalid SFTP path: '{internalPath}'. Expected 'user@host[:port]/path'.",
                nameof(internalPath));
        return parsed;
    }

    internal static bool TryParseInternalPath(string internalPath, out (string user, string host, int port, string remotePath) result)
    {
        result = default;
        if (string.IsNullOrEmpty(internalPath))
            return false;

        var atIdx = internalPath.IndexOf('@');
        if (atIdx <= 0)
            return false;
        var user = internalPath[..atIdx];

        var rest = internalPath[(atIdx + 1)..];
        if (rest.Length == 0)
            return false;

        // 分隔 host[:port] 与 remote path: 找第一个 '/'
        var slashIdx = rest.IndexOf('/');
        var hostPort = slashIdx < 0 ? rest : rest[..slashIdx];
        var remotePath = slashIdx < 0 ? "" : rest[slashIdx..];

        if (string.IsNullOrEmpty(hostPort))
            return false;

        // 拆分 host 与 port。注: 不支持 IPv6 字面量 (如 [::1]:22), M4 阶段先不支持。
        var colonIdx = hostPort.IndexOf(':');
        string host;
        int port;
        if (colonIdx < 0)
        {
            host = hostPort;
            port = 22;
        }
        else
        {
            host = hostPort[..colonIdx];
            var portStr = hostPort[(colonIdx + 1)..];
            if (!int.TryParse(portStr, out port) || port < 1 || port > 65535)
                return false;
        }

        if (string.IsNullOrEmpty(host))
            return false;

        result = (user, host, port, remotePath);
        return true;
    }

    /// <summary>规范化 remote path: 必须以 '/' 开头, 折叠重复分隔符, 末尾不保留 '/' (除根目录外)。</summary>
    private static string NormalizeRemotePath(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
            return "/";

        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "/";

        // 折叠 "." 与 ".." (M4: 仅基础规范化, 不解析符号链接)。
        var stack = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        return "/" + string.Join('/', stack);
    }

    /// <summary>取 remote path 的父目录 (规范化的)。根目录返回空串。</summary>
    private static string GetParentRemote(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
            return "";
        var trimmed = remotePath.TrimEnd('/');
        var lastSep = trimmed.LastIndexOf('/');
        return lastSep <= 0 ? "/" : trimmed[..lastSep];
    }

    /// <summary>
    /// 递归创建远程目录。SSH.NET 2025.x 的 <see cref="SftpClient.CreateDirectory(string)"/>
    /// 不会自动创建中间目录, 且已存在时会抛异常, 这里两者都处理。
    /// </summary>
    private static void EnsureRemoteDirectory(SftpClient client, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
            return;

        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var seg in segments)
        {
            current = current + "/" + seg;
            try
            {
                // 已存在则跳过; GetAttributes 抛 SftpPathNotFoundException 说明不存在。
                client.GetAttributes(current);
                continue;
            }
            catch (SftpPathNotFoundException)
            {
                // 不存在 → 创建。
            }
            try
            {
                client.CreateDirectory(current);
            }
            catch (SftpPermissionDeniedException)
            {
                // 父级无写权限, 后续也无法创建, 直接放弃 (OpenWrite 调用方会再失败)。
                return;
            }
        }
    }

    // ---- Item 转换 helpers ----

    private static Item ToItem(ItemPath path, SftpFileAttributes attrs)
    {
        var kind = attrs.IsDirectory ? ItemKind.Directory
            : attrs.IsSymbolicLink ? ItemKind.SymbolicLink
            : ItemKind.File;
        return new Item
        {
            Path = path,
            Kind = kind,
            Size = attrs.IsDirectory ? null : attrs.Size,
            // SSH.NET 返回的 DateTime 是 UTC, 显式标注 Kind 后转 DateTimeOffset。
            Timestamps = new ItemTimestamps(
                Created: null,
                Modified: ToDateTimeOffset(attrs.LastWriteTimeUtc),
                Accessed: ToDateTimeOffset(attrs.LastAccessTimeUtc)),
        };
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime value)
    {
        if (value == default)
            return null;
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static bool MatchesGlob(string name, string filter)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, name, ignoreCase: true);

    /// <summary>识别 "路径不存在" 类异常。SSH.NET 各版本异常类型略有差异, 这里宽泛匹配。</summary>
    private static bool IsNotFound(Exception ex)
        => ex is SftpPathNotFoundException
            || ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// SFTP Provider 抛出的领域异常。Per ADR-0026: 包一层 OpenShellException 以便上层 ErrorRecord 映射。
/// </summary>
internal sealed class SftpProviderException : OpenShellException
{
    public SftpProviderException(string message, ErrorCategory category) : base(message)
    {
        Category = category;
    }

    public SftpProviderException(string message, ErrorCategory category, Exception innerException)
        : base(message, innerException)
    {
        Category = category;
    }

    public override ErrorCategory Category { get; }
}

/// <summary>
/// SSH.NET <see cref="SftpClient"/> 连接池。Per ADR-0019 §10: 远程 Provider 必须 IDisposable。
/// 按 user@host:port 缓存连接实例, 同一连接的并发操作通过 <see cref="SemaphoreSlim"/> 串行化
/// (SSH.NET 的 SftpClient 非线程安全, 多线程并发调用同一实例会破坏协议状态)。
/// </summary>
internal sealed class SftpConnectionPool : IDisposable
{
    private readonly ICredentialProvider _credProvider;
    private readonly Dictionary<string, PooledConnection> _connections = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _disposed;

    public SftpConnectionPool(ICredentialProvider credProvider)
    {
        _credProvider = credProvider;
    }

    /// <summary>
    /// 在 (user, host, port) 对应的连接上执行同步 <paramref name="action"/>, 用 Task.Run 包装为异步。
    /// 若连接尚未建立或已断开, 先 Connect; 鉴权失败 / 网络错误抛 <see cref="SftpProviderException"/>。
    /// </summary>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        string user, string host, int port,
        Func<SftpClient, TResult> action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conn = GetOrCreate(user, host, port);
        await conn.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!conn.Client.IsConnected)
            {
                await ConnectAsync(conn.Client, user, host, port, cancellationToken).ConfigureAwait(false);
            }
            return await Task.Run(() => action(conn.Client), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            conn.Semaphore.Release();
        }
    }

    /// <summary>无返回值的 ExecuteAsync 重载。</summary>
    public async ValueTask ExecuteAsync(
        string user, string host, int port,
        Action<SftpClient> action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conn = GetOrCreate(user, host, port);
        await conn.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!conn.Client.IsConnected)
            {
                await ConnectAsync(conn.Client, user, host, port, cancellationToken).ConfigureAwait(false);
            }
            await Task.Run(() => action(conn.Client), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            conn.Semaphore.Release();
        }
    }

    private PooledConnection GetOrCreate(string user, string host, int port)
    {
        var key = $"{user}@{host}:{port}";
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connections.TryGetValue(key, out var existing) && !existing.IsDisposed)
                return existing;

            var cred = _credProvider.GetCredentials(host, user);
            if (cred is null)
            {
                throw new SftpProviderException(
                    $"No SFTP credentials found for {user}@{host}:{port}. "
                    + "Run 'set-sftpcredential -Host <host> -User <user> -Password <pw>' to configure.",
                    ErrorCategory.AuthenticationFailed);
            }

            var client = CreateClient(host, port, user, cred);
            var conn = new PooledConnection(key, client);
            _connections[key] = conn;
            return conn;
        }
    }

    private static SftpClient CreateClient(string host, int port, string user, SftpCredentials cred)
    {
        try
        {
            if (!string.IsNullOrEmpty(cred.PrivateKeyPath))
            {
                var keyFile = !string.IsNullOrEmpty(cred.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(cred.PrivateKeyPath, cred.PrivateKeyPassphrase)
                    : new PrivateKeyFile(cred.PrivateKeyPath);
                return new SftpClient(host, port, user, keyFile);
            }
            if (!string.IsNullOrEmpty(cred.Password))
            {
                return new SftpClient(host, port, user, cred.Password);
            }

            throw new SftpProviderException(
                $"SFTP credentials for {user}@{host}:{port} have neither password nor private key path. "
                + "Re-configure with set-sftpcredential.",
                ErrorCategory.AuthenticationFailed);
        }
        catch (SftpProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SftpProviderException(
                $"Failed to create SFTP client for {user}@{host}:{port}: {ex.Message}",
                ErrorCategory.AuthenticationFailed,
                ex);
        }
    }

    private static async ValueTask ConnectAsync(
        SftpClient client, string user, string host, int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // SSH.NET 的 Connect 是同步阻塞调用, 用 Task.Run 释放调用线程。
            await Task.Run(() => client.Connect(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SshAuthenticationException ex)
        {
            throw new SftpProviderException(
                $"SFTP authentication failed for {user}@{host}:{port}: {ex.Message}",
                ErrorCategory.AuthenticationFailed,
                ex);
        }
        catch (SshConnectionException ex)
        {
            throw new SftpProviderException(
                $"SFTP connection failed for {user}@{host}:{port}: {ex.Message}",
                ErrorCategory.NetworkError,
                ex);
        }
        catch (Exception ex)
        {
            throw new SftpProviderException(
                $"SFTP connect failed for {user}@{host}:{port}: {ex.Message}",
                ErrorCategory.NetworkError,
                ex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var conn in _connections.Values)
            {
                conn.Dispose();
            }
            _connections.Clear();
        }
    }

    /// <summary>
    /// IH-006: 故障注入测试入口——断开并移除所有池化连接。
    /// 下一次 <see cref="ExecuteAsync{TResult}"/> 会重新建连, 用于验证断线重连行为。
    /// </summary>
    internal void DisconnectAll()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var conn in _connections.Values)
                conn.Dispose();
            _connections.Clear();
        }
    }

    private sealed class PooledConnection : IDisposable
    {
        public string Key { get; }
        public SftpClient Client { get; }
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public PooledConnection(string key, SftpClient client)
        {
            Key = key;
            Client = client;
        }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            try { Client.Disconnect(); } catch { /* ignore */ }
            try { Client.Dispose(); } catch { /* ignore */ }
            Semaphore.Dispose();
        }
    }
}
