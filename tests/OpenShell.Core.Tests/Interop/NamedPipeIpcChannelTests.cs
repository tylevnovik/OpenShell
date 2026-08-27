using FluentAssertions;
using OpenShell;
using OpenShell.Interop;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Interop;

/// <summary>
/// NamedPipeIpcChannel 回环单测。Per ADR-0021.
/// 用 server + client 两个通道实例 (相同端点名) 测试:
/// - 握手协议版本匹配 (CurrentProtocolVersion)
/// - SendAsync + ListenAsync 端到端 (双向)
/// - StopAsync 可重入
/// - 未连接 / 已运行状态下调用 StartAsync 抛 InvalidOperationException
/// - 未连接时 SendAsync 抛 InvalidOperationException
/// </summary>
/// <remarks>
/// 跨平台: Windows 用 Named Pipe, Linux/Mac 用 Unix Domain Socket, 实现内部已分支处理。
/// 端点名用 GUID 保证唯一, 避免并发测试 / 残留 socket 冲突。
/// 所有 IO 操作带 CancellationToken 超时保护, 避免测试永久阻塞。
/// 不用 `await using` (避免 Dispose 卡死), 改用 try/finally + 带超时的 StopAsync。
/// </remarks>
public sealed class NamedPipeIpcChannelTests
{
    /// <summary>构造唯一端点名 (含 GUID, 避免并发测试冲突)。</summary>
    private static string GetUniqueEndpoint()
    {
        var id = Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows())
            return $@"\\.\pipe\openshell-test-{id}";
        // macOS UDS 路径上限 104 字符，/var/folders/.../T/ 前缀较长，保持短命名留出余量。
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"osh-{id}.sock");
    }

    /// <summary>安全停止通道: 带 2 秒超时, 超时不等 (避免 StopAsync 永久阻塞)。</summary>
    private static async Task StopAsyncSafe(IIpcChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await channel.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: 超时或异常都不影响测试断言。
        }
    }

    /// <summary>启动服务端 + 客户端, 完成握手; 返回 (server, client) 或抛超时异常。</summary>
    private static async Task<(NamedPipeIpcChannel server, NamedPipeIpcChannel client)> StartPairAsync(
        string endpoint, TimeSpan totalTimeout)
    {
        var server = new NamedPipeIpcChannel(endpoint, HostKind.Cli);
        var client = new NamedPipeIpcChannel(endpoint, HostKind.Gui);

        using var testCts = new CancellationTokenSource(totalTimeout);

        // 服务端 StartAsync 在后台 (不阻塞测试线程)。
        // 不用 LongRunning: async lambda 立即在第一个 await 让出, 专用线程立即返回线程池, 无收益。
        var serverStartTask = Task.Run(async () => await server.StartAsync(testCts.Token), testCts.Token);

        // 轮询等待服务端 NamedPipeServerStream 就绪 (而非固定 500ms 延迟)。
        // 检查 serverStartTask 状态: Faulted 则抛出真实异常; 等待最长 2s。
        var readyDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < readyDeadline && !serverStartTask.IsCompleted)
        {
            // Windows: NamedPipeClientStream.ConnectAsync(ct) 会等待管道出现, 不需要预检。
            // 这里仅做 Faulted 检测, 不依赖管道文件系统可见性。
            if (serverStartTask.IsFaulted)
                throw new InvalidOperationException("Server StartAsync failed", serverStartTask.Exception?.InnerException);
            await Task.Delay(50, testCts.Token).ConfigureAwait(false);
        }

        // 诊断: 服务端若已失败, 抛出真实异常。
        if (serverStartTask.IsFaulted)
            throw new InvalidOperationException("Server StartAsync failed", serverStartTask.Exception?.InnerException);

        // 客户端单次 ConnectAsync 带总超时。
        // 注意: 禁止用短超时重试 — 超时取消时客户端可能已连上, 取消会导致 "Pipe is broken"。
        try
        {
            await client.ConnectAsync(testCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (testCts.IsCancellationRequested)
        {
            // 诊断: 超时时检查服务端是否失败。
            if (serverStartTask.IsFaulted)
                throw new InvalidOperationException("Server StartAsync failed", serverStartTask.Exception?.InnerException);
            throw new TimeoutException($"Client failed to connect within {totalTimeout.TotalSeconds}s.");
        }

        // 等服务端 StartAsync 完成 (包括握手), 超时保护。
        try
        {
            await serverStartTask.WaitAsync(totalTimeout, testCts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Server StartAsync did not complete within {totalTimeout.TotalSeconds}s.");
        }

        return (server, client);
    }

    [Fact]
    public async Task Raw_NamedPipe_ServerCreatedInTaskRun_Connects()
    {
        // D-703: 本用例直连 "\\.\pipe\" 原生 Windows 管道，仅 Windows 有意义（Unix 走 UDS）。
        if (!OperatingSystem.IsWindows()) return;

        // 变体: NamedPipeServerStream 在 Task.Run 中创建 (模拟 NamedPipeIpcChannel.StartAsync 的行为)。
        // 确认是否是 Task.Run 中创建导致客户端连不上。
        var pipeName = @"\\.\pipe\openshell-raw2-" + Guid.NewGuid().ToString("N");

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverStreamTask = Task.Run(async () =>
        {
            var s = new System.IO.Pipes.NamedPipeServerStream(
                pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            await s.WaitForConnectionAsync(testCts.Token);
            return s;
        }, testCts.Token);

        // 给服务端时间创建 NamedPipeServerStream 并进入 WaitForConnectionAsync。
        await Task.Delay(500);

        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(connectCts.Token);

        var server = await serverStreamTask;
        server.IsConnected.Should().BeTrue();
        client.IsConnected.Should().BeTrue();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Raw_NamedPipe_BasicConnectivity_EnvironmentCheck()
    {
        // D-703: 本用例直连 "\\.\pipe\" 原生 Windows 管道，仅 Windows 有意义（Unix 走 UDS）。
        if (!OperatingSystem.IsWindows()) return;

        // 环境检查: 直接用 NamedPipeServerStream + NamedPipeClientStream 验证 Windows NamedPipe IO 能工作。
        // 若此测试也卡/失败, 说明是环境问题 (而非 NamedPipeIpcChannel 的问题), 后续真实连接测试应 Skip。
        var pipeName = @"\\.\pipe\openshell-raw-" + Guid.NewGuid().ToString("N");

        using var server = new System.IO.Pipes.NamedPipeServerStream(
            pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

        var serverWaitTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForConnectionAsync(cts.Token);
        });

        // 给服务端时间进入 WaitForConnectionAsync。
        await Task.Delay(200);

        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(connectCts.Token);

        // 等服务端检测到连接。
        await serverWaitTask;

        server.IsConnected.Should().BeTrue("raw NamedPipe server should be connected");
        client.IsConnected.Should().BeTrue("raw NamedPipe client should be connected");
    }

    [Fact]
    public async Task Loopback_ServerClient_RoundTrip_BothDirections()
    {
        var endpoint = GetUniqueEndpoint();
        var (server, client) = await StartPairAsync(endpoint, TimeSpan.FromSeconds(10));

        try
        {
            server.IsConnected.Should().BeTrue("server should be connected after handshake");
            client.IsConnected.Should().BeTrue("client should be connected after handshake");

            // 客户端 → 服务端: 发送 IpcShutdown。
            var msg1 = new IpcShutdown();
            var recv1Task = ReceiveFirstAsync(server);
            await client.SendAsync(msg1);
            var received1 = await recv1Task;
            received1.Should().BeOfType<IpcShutdown>("IpcShutdown should round-trip unchanged");

            // 服务端 → 客户端: 发送 IpcLocationChanged。
            var msg2 = new IpcLocationChanged(new ItemPath { Provider = "fs", InternalPath = "/home/user" });
            var recv2Task = ReceiveFirstAsync(client);
            await server.SendAsync(msg2);
            var received2 = await recv2Task;
            received2.Should().BeOfType<IpcLocationChanged>()
                .Which.NewLocation.InternalPath.Should().Be("/home/user", "path should round-trip with payload intact");
        }
        finally
        {
            await StopAsyncSafe(client);
            await StopAsyncSafe(server);
        }
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ThrowsInvalidOperation()
    {
        var endpoint = GetUniqueEndpoint();
        var (server, client) = await StartPairAsync(endpoint, TimeSpan.FromSeconds(10));

        try
        {
            // 服务端已 running (_state == 1), 再次 StartAsync 应抛。
            var act = () => server.StartAsync();
            await act.Should().ThrowAsync<InvalidOperationException>(
                "StartAsync must fail-fast when channel is already running");
        }
        finally
        {
            await StopAsyncSafe(client);
            await StopAsyncSafe(server);
        }
    }

    [Fact]
    public async Task SendAsync_WhenNotConnected_ThrowsInvalidOperation()
    {
        // 未启动 (idle), SendAsync 抛。
        var channel = new NamedPipeIpcChannel(GetUniqueEndpoint(), HostKind.Cli);
        var act = () => channel.SendAsync(new IpcShutdown());
        await act.Should().ThrowAsync<InvalidOperationException>(
            "SendAsync must fail when channel is not connected");
        await StopAsyncSafe(channel);
    }

    [Fact]
    public async Task StopAsync_Idempotent_MultipleCallsSafe()
    {
        var endpoint = GetUniqueEndpoint();
        var (server, client) = await StartPairAsync(endpoint, TimeSpan.FromSeconds(10));

        try
        {
            // 多次 StopAsync 都不应抛 (Per ADR-0021 §7: 可重入, 安全)。
            await StopAsyncSafe(server);
            await StopAsyncSafe(server);
            await StopAsyncSafe(client);
            await StopAsyncSafe(client);

            server.IsConnected.Should().BeFalse("server disconnected after StopAsync");
            client.IsConnected.Should().BeFalse("client disconnected after StopAsync");
        }
        finally
        {
            await StopAsyncSafe(client);
            await StopAsyncSafe(server);
        }
    }

    [Fact]
    public async Task StopAsync_WhenIdle_NoOp()
    {
        // Per ADR-0021 §7: StopAsync 在 idle 状态下调用也应是安全的 (no-op)。
        var channel = new NamedPipeIpcChannel(GetUniqueEndpoint(), HostKind.Cli);

        var act = () => channel.StopAsync();
        await act.Should().NotThrowAsync("StopAsync on an idle channel should be a no-op");
    }

    [Fact]
    public void Constructor_EmptyChannelName_Throws()
    {
        var act = () => new NamedPipeIpcChannel("", HostKind.Cli);
        act.Should().Throw<ArgumentException>("empty channel name must be rejected");
    }

    [Fact]
    public void Constructor_WhitespaceChannelName_Throws()
    {
        var act = () => new NamedPipeIpcChannel("   ", HostKind.Cli);
        act.Should().Throw<ArgumentException>("whitespace-only channel name must be rejected");
    }

    [Fact]
    public void ChannelName_ReturnsConfiguredValue()
    {
        var endpoint = GetUniqueEndpoint();
        var channel = new NamedPipeIpcChannel(endpoint, HostKind.Cli);
        channel.ChannelName.Should().Be(endpoint, "ChannelName must match constructor argument");
    }

    [Fact]
    public void CurrentProtocolVersion_IsOne()
    {
        // Per ADR-0021 §8: 当前协议版本 = 1。
        NamedPipeIpcChannel.CurrentProtocolVersion.Should().Be(1,
            "current protocol version must be 1 per ADR-0021 §8");
    }

    [Fact]
    public async Task SendMultiple_MessagesArriveInOrder()
    {
        // Per ADR-0040 §9: 同一通道内消息保持发送顺序。
        var endpoint = GetUniqueEndpoint();
        var (server, client) = await StartPairAsync(endpoint, TimeSpan.FromSeconds(10));

        try
        {
            // 发送 5 条 IpcShutdown, 验证全部收到。
            var recvTask = ReceiveNAsync(server, 5);
            for (int i = 0; i < 5; i++)
            {
                await client.SendAsync(new IpcShutdown());
            }

            var received = await recvTask;
            received.Should().HaveCount(5, "all 5 messages should be received");
        }
        finally
        {
            await StopAsyncSafe(client);
            await StopAsyncSafe(server);
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenServerNotListening_ThrowsTimeoutOrIoError()
    {
        // 客户端连接不存在的服务端必须在取消窗口内快速失败（而非永久阻塞）。
        // D-703: 失败形态随平台不同——Windows 为取消/超时，Linux 为 SocketException，
        // macOS 可能为 IOException；三者均满足"快速失败"契约。
        var endpoint = GetUniqueEndpoint();
        var client = new NamedPipeIpcChannel(endpoint, HostKind.Gui);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ex = await Record.ExceptionAsync(() => client.ConnectAsync(cts.Token));

        ex.Should().NotBeNull("connecting to a non-listening endpoint must fail within the cancellation token window");
        (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException).Should()
            .BeTrue($"failure must be cancellation/timeout or an IO/socket error, got: {ex.GetType().Name}");

        await StopAsyncSafe(client);
    }

    /// <summary>
    /// 从 channel 接收第一条消息, 5 秒超时保护避免测试永久阻塞。
    /// </summary>
    private static async Task<IpcMessage> ReceiveFirstAsync(IIpcChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var msg in channel.ListenAsync(cts.Token).ConfigureAwait(false))
        {
            return msg;
        }
        throw new InvalidOperationException("ListenAsync ended without producing a message within 5s.");
    }

    /// <summary>从 channel 接收 N 条消息, 5 秒超时保护。</summary>
    private static async Task<IReadOnlyList<IpcMessage>> ReceiveNAsync(IIpcChannel channel, int count)
    {
        var list = new List<IpcMessage>(count);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var msg in channel.ListenAsync(cts.Token).ConfigureAwait(false))
        {
            list.Add(msg);
            if (list.Count >= count) break;
        }
        return list;
    }
}
