using System.Buffers;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace OpenShell.Interop;

/// <summary>
/// 基于 Named Pipe (Windows) / Unix Domain Socket (Linux/Mac) 的 <see cref="IIpcChannel"/> 实现。Per ADR-0021.
/// 协议: [4 字节 big-endian 长度][UTF-8 JSON payload]。
/// 内部用 <see cref="System.Threading.Channels.Channel{T}"/> 做 inbox/outbox 缓冲, send/receive 循环解耦。
/// 服务端模式: <see cref="StartAsync"/> 创建 endpoint 并 accept 一个连接。
/// 客户端模式: <see cref="ConnectAsync"/> 连接到已存在的 endpoint。
/// </summary>
public sealed class NamedPipeIpcChannel : IIpcChannel
{
    /// <summary>当前 IPC 协议版本。Per ADR-0021 §8.</summary>
    public const int CurrentProtocolVersion = 1;

    private const int LengthPrefixSize = 4;
    private const int MaxMessageBytes = 64 * 1024 * 1024; // 64 MB 上限, 防止恶意/异常长度前缀导致 OOM

    private readonly string _channelName;
    private readonly HostKind _sourceKind;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<IpcMessage> _outbox = Channel.CreateUnbounded<IpcMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Channel<IpcMessage> _inbox = Channel.CreateUnbounded<IpcMessage>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });

    private Stream? _stream;
    private Task? _receiveLoop;
    private Task? _sendLoop;
    private int _state; // 0 = idle, 1 = running, 2 = stopped

    /// <summary>构造 NamedPipeIpcChannel, 使用 <see cref="IpcEndpoints.GetEndpointName()"/> 的默认端点名。</summary>
    /// <param name="sourceKind">本端 host 类型 (Cli/GUI), 用于握手消息。默认 Cli。</param>
    public NamedPipeIpcChannel(HostKind sourceKind = HostKind.Cli) : this(IpcEndpoints.GetEndpointName(), sourceKind) { }

    /// <summary>构造 NamedPipeIpcChannel。</summary>
    /// <param name="channelName">通道名称 (含 sessionId, 如 "\\.\pipe\openshell-1" 或 "/tmp/openshell-1.sock")。</param>
    /// <param name="sourceKind">本端 host 类型 (Cli/GUI), 用于握手消息。默认 Cli。</param>
    public NamedPipeIpcChannel(string channelName, HostKind sourceKind = HostKind.Cli)
    {
        if (string.IsNullOrWhiteSpace(channelName))
            throw new ArgumentException("Channel name cannot be empty.", nameof(channelName));
        _channelName = channelName;
        _sourceKind = sourceKind;
    }

    /// <inheritdoc />
    public string ChannelName => _channelName;

    /// <inheritdoc />
    public bool IsConnected => _stream is not null && _stream.CanRead && _stream.CanWrite && _state == 1;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureIdle();

        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            // Windows: NamedPipeServerStream, 单实例, 异步模式。
            var server = new NamedPipeServerStream(
                _channelName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            stream = server;
        }
        else
        {
            // Linux/Mac: Unix Domain Socket。
            if (File.Exists(_channelName))
                File.Delete(_channelName);

            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_channelName));
            listener.Listen(backlog: 1);
            var accepted = await listener.AcceptAsync(ct).ConfigureAwait(false);
            stream = new NetworkStream(accepted, ownsSocket: true);
        }

        _stream = stream;
        try
        {
            await PerformHandshakeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _state = 1;
        StartLoops();
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureIdle();

        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            // Windows: NamedPipeClientStream。
            var client = new NamedPipeClientStream(
                serverName: ".",
                _channelName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            stream = client;
        }
        else
        {
            // Linux/Mac: Unix Domain Socket。
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_channelName), ct).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        _stream = stream;
        try
        {
            await PerformHandshakeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _state = 1;
        StartLoops();
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        // 可重入: 多次调用安全。
        if (Interlocked.Exchange(ref _state, 2) == 2)
            return;

        _cts.Cancel();
        _outbox.Writer.TryComplete();
        _inbox.Writer.TryComplete();

        var stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        // 等待 send/receive 循环退出 (忽略取消异常)。
        var tasks = new List<Task>(2);
        if (_receiveLoop is not null) tasks.Add(_receiveLoop);
        if (_sendLoop is not null) tasks.Add(_sendLoop);
        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (IOException) { /* peer disconnected */ }
            catch (SocketException) { /* peer disconnected */ }
        }

        _receiveLoop = null;
        _sendLoop = null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IpcMessage> ListenAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 合并外部 ct 与内部 _cts: 任一触发则停止迭代。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await foreach (var msg in _inbox.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
        {
            yield return msg;
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_state != 1)
            throw new InvalidOperationException("IPC channel is not connected.");
        await _outbox.Writer.WriteAsync(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }

    private void EnsureIdle()
    {
        if (_state != 0)
            throw new InvalidOperationException($"IPC channel is already in state {_state} (expected idle).");
    }

    private void StartLoops()
    {
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _sendLoop = Task.Run(SendLoopAsync);
    }

    /// <summary>
    /// 连接建立后的握手交换。Per ADR-0021 §8.
    /// 非对称协议: 服务端先写后读, 客户端先读后写 (避免双方同时 WriteAsync 触发 .NET NamedPipe 的内部取消)。
    /// 双方互发 IpcHandshake (不经过 inbox/outbox, 直接操作 stream, 避免与 send/receive 循环竞争)。
    /// ProtocolVersion 必须匹配, 不匹配 fail-fast。5 秒超时。
    /// 必须在 <see cref="StartLoops"/> 之前调用 (此时 stream 已建立, inbox/outbox 空)。
    /// </summary>
    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Stream not established.");

        // 服务端 (Cli) 先写后读; 客户端 (Gui) 先读后写。
        // 避免双方同时 WriteAsync 在 .NET NamedPipe 实现下触发 OperationCanceledException。
        IpcHandshake ourHandshake = new(CurrentProtocolVersion, _sourceKind, Guid.NewGuid());
        IpcHandshake peerHandshake;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        if (_sourceKind == HostKind.Cli)
        {
            // 服务端: 先发送, 再接收。
            await WriteMessageAsync(stream, ourHandshake, ct).ConfigureAwait(false);
            var peerMsg = await ReadMessageAsync(stream, timeoutCts.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Expected IpcHandshake from peer, got EOF.");
            peerHandshake = peerMsg as IpcHandshake
                ?? throw new InvalidDataException($"Expected IpcHandshake from peer, got {peerMsg.GetType().Name}.");
        }
        else
        {
            // 客户端: 先接收, 再发送。
            var peerMsg = await ReadMessageAsync(stream, timeoutCts.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Expected IpcHandshake from peer, got EOF.");
            peerHandshake = peerMsg as IpcHandshake
                ?? throw new InvalidDataException($"Expected IpcHandshake from peer, got {peerMsg.GetType().Name}.");
            await WriteMessageAsync(stream, ourHandshake, ct).ConfigureAwait(false);
        }

        if (peerHandshake.ProtocolVersion != CurrentProtocolVersion)
            throw new InvalidOperationException(
                $"IPC protocol version mismatch: local={CurrentProtocolVersion}, remote={peerHandshake.ProtocolVersion}. "
                + "Please upgrade OpenShell to the same version on both sides.");
    }

    /// <summary>接收循环: 持续读取长度前缀 JSON, 写入 inbox channel。</summary>
    private async Task ReceiveLoopAsync()
    {
        var stream = _stream;
        if (stream is null) return;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await ReadMessageAsync(stream, _cts.Token).ConfigureAwait(false);
                if (msg is null)
                {
                    // 对端关闭连接。
                    break;
                }
                await _inbox.Writer.WriteAsync(msg, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (IOException) { /* peer disconnected */ }
        catch (SocketException) { /* peer disconnected */ }
        finally
        {
            // 通知 ListenAsync 的消费者: 不再有新消息。
            _inbox.Writer.TryComplete();
        }
    }

    /// <summary>发送循环: 从 outbox channel 读取消息, 写入 stream。</summary>
    private async Task SendLoopAsync()
    {
        var stream = _stream;
        if (stream is null) return;

        try
        {
            await foreach (var msg in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await WriteMessageAsync(stream, msg, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (IOException) { /* peer disconnected */ }
        catch (SocketException) { /* peer disconnected */ }
        finally
        {
            // outbox 已 complete 或 stream 异常: 通知 StopAsync。
        }
    }

    /// <summary>
    /// 读取一条消息: 4 字节 big-endian 长度 + JSON payload。
    /// 返回 null 表示对端关闭连接 (EOF)。
    /// </summary>
    private static async Task<IpcMessage?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        // 读 4 字节长度前缀 (big-endian)。
        var lengthBytes = ArrayPool<byte>.Shared.Rent(LengthPrefixSize);
        try
        {
            if (!await ReadExactAsync(stream, lengthBytes, LengthPrefixSize, ct).ConfigureAwait(false))
                return null; // EOF

            var length = ReadInt32BigEndian(lengthBytes);
            if (length < 0 || length > MaxMessageBytes)
                throw new InvalidDataException($"IPC message length {length} is out of range (0..{MaxMessageBytes}).");

            // 读 JSON payload。
            var payload = length == 0 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (length > 0 && !await ReadExactAsync(stream, payload, length, ct).ConfigureAwait(false))
                    return null; // EOF mid-message

                var json = Encoding.UTF8.GetString(payload, 0, length);
                var msg = IpcMessageJsonContext.Deserialize(json);
                if (msg is null)
                    throw new InvalidDataException("Failed to deserialize IPC message (null result).");
                return msg;
            }
            finally
            {
                if (length > 0) ArrayPool<byte>.Shared.Return(payload);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthBytes);
        }
    }

    /// <summary>写入一条消息: 4 字节 big-endian 长度 + UTF-8 JSON payload。</summary>
    private static async Task WriteMessageAsync(Stream stream, IpcMessage message, CancellationToken ct)
    {
        var json = IpcMessageJsonContext.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);

        if (payload.Length > MaxMessageBytes)
            throw new InvalidOperationException($"IPC message exceeds {MaxMessageBytes} bytes.");

        // 4 字节 big-endian 长度前缀 + payload 拼成单个 buffer 一次写入。
        // 避免分两次 WriteAsync 在某些 NamedPipe 实现下导致 "Pipe is broken" 异常。
        var frame = new byte[LengthPrefixSize + payload.Length];
        WriteInt32BigEndian(frame, payload.Length);
        if (payload.Length > 0)
            Buffer.BlockCopy(payload, 0, frame, LengthPrefixSize, payload.Length);

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>精确读取 n 字节; 返回 false 表示 EOF (对端关闭)。</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n == 0) return false; // EOF
            offset += n;
        }
        return true;
    }

    private static int ReadInt32BigEndian(byte[] buffer)
        => (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];

    private static void WriteInt32BigEndian(byte[] buffer, int value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }
}
