using FluentAssertions;
using OpenShell;
using OpenShell.Events;
using OpenShell.Interop;
using OpenShell.Items;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Events;

/// <summary>
/// CrossProcessEventBridge 单测。Per ADR-0040 §4.
/// 验证:
/// - 本地发布的 IRemoteRoutableEvent 被转发到对端通道 (通过 IpcEventMessage)
/// - 对端转发来的事件被重新 Publish 到本地 bus (携带 OriginHostId = 本地 host)
/// - 防回环机制: 已被对端标记过 OriginHostId 的事件不再转发回去
/// - 非 IRemoteRoutableEvent 事件 (如 SessionStartedEvent) 不转发
/// </summary>
public sealed class CrossProcessEventBridgeTests
{
    /// <summary>本端 host id (用于防回环)。</summary>
    private const string LocalHostId = "local-host";

    [Fact]
    public async Task StartAsync_SubscribesAllRemoteRoutableEventTypes()
    {
        // Per ADR-0040 §4: bridge 必须订阅全部 13 个 IRemoteRoutableEvent 子类。
        // StartAsync 成功 + DisposeAsync 安全 → 订阅路径正常。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task ForwardIfRemote_RemoteRoutableEvent_ForwardedToChannel()
    {
        // 发布 ItemCreatedEvent → bridge 应通过 channel.SendAsync 转发 IpcEventMessage。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();
        await WaitAsync();

        var path = new ItemPath { Provider = "fs", InternalPath = "/test/file.txt" };
        var item = Item.File(path);
        var evt = new ItemCreatedEvent { Path = path, Item = item };

        bus.Publish(evt);
        await WaitAsync();

        channel.SentMessages.Should().ContainSingle(
            x => x is IpcEventMessage,
            "ItemCreatedEvent must be forwarded as IpcEventMessage");

        var forwarded = (IpcEventMessage)channel.SentMessages[0];
        forwarded.EventType.Should().Contain(nameof(ItemCreatedEvent),
            "EventType should be assembly-qualified name of ItemCreatedEvent");
        forwarded.Payload.Should().Contain("/test/file.txt",
            "Payload should contain serialized event data");

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task ForwardIfRemote_NonRemoteRoutableEvent_NotForwarded()
    {
        // Per ADR-0040 §4: 仅 IRemoteRoutableEvent 子类转发。SessionStartedEvent 不转发。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();
        await WaitAsync();

        // SessionStartedEvent 仅实现 IEvent, 不实现 IRemoteRoutableEvent。
        bus.Publish(new SessionStartedEvent { HostKind = HostKind.Cli, SessionId = Guid.NewGuid() });
        await WaitAsync();

        channel.SentMessages.Should().BeEmpty(
            "non-IRemoteRoutableEvent must not be forwarded across processes");

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task ForwardIfRemote_EventWithForeignOriginHostId_NotReForwarded()
    {
        // 防回环: 事件已携带非本地 OriginHostId, 说明来自对端, 不应再转发回去。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();
        await WaitAsync();

        var path = new ItemPath { Provider = "fs", InternalPath = "/test" };
        var evt = new ItemCreatedEvent
        {
            Path = path,
            Item = Item.File(path),
            OriginHostId = "remote-host", // 非 null 且 != LocalHostId
        };

        bus.Publish(evt);
        await WaitAsync();

        channel.SentMessages.Should().BeEmpty(
            "event already forwarded from remote host must not be re-forwarded (防回环)");

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task ForwardIfRemote_EventWithLocalOriginHostId_ForwardedOnce()
    {
        // OriginHostId == LocalHostId: 本端先发布的事件, 应正常转发 (首次转发场景)。
        // 代码逻辑: "非空且 != 本地" 才跳过, 所以 == 本地时转发。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();
        await WaitAsync();

        var path = new ItemPath { Provider = "fs", InternalPath = "/local" };
        var evt = new ItemCreatedEvent
        {
            Path = path,
            Item = Item.File(path),
            OriginHostId = LocalHostId, // == 本地, 首次转发
        };

        bus.Publish(evt);
        await WaitAsync();

        channel.SentMessages.Should().ContainSingle(
            x => x is IpcEventMessage,
            "event with OriginHostId == local host (first publish) must be forwarded");

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task StartAsync_BridgeReceivesIpcEventMessage_RepublishesLocally()
    {
        // Per ADR-0040 §4: 对端转发的 IpcEventMessage 被 bridge 接收, 反序列化后重新 Publish 到本地 bus。
        // 验证: 注入 IpcEventMessage 到 channel 的 inbox → 本地订阅者收到反序列化后的事件,
        //       且事件 OriginHostId 被设置为本地 host id (防回环)。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        // 本地订阅 ItemDeletedEvent, 等待 bridge 转发进来。
        ItemDeletedEvent? received = null;
        bus.Subscribe<ItemDeletedEvent>(e => received = e);

        await bridge.StartAsync();

        // 构造对端转发过来的 IpcEventMessage: ItemDeletedEvent 序列化。
        var path = new ItemPath { Provider = "fs", InternalPath = "/deleted.txt" };
        var remoteEvt = new ItemDeletedEvent
        {
            Path = path,
            Item = Item.File(path),
            OriginHostId = "remote-host", // 对端先标记的
        };

        var eventType = remoteEvt.GetType().AssemblyQualifiedName!;
        var payload = System.Text.Json.JsonSerializer.Serialize(remoteEvt, remoteEvt.GetType(), IpcMessageJsonContext.Options);
        channel.InjectInbound(new IpcEventMessage(eventType, payload));

        await WaitAsync();

        received.Should().NotBeNull("bridge should republish received IpcEventMessage to local bus");
        received!.Path.InternalPath.Should().Be("/deleted.txt");
        received.OriginHostId.Should().Be(LocalHostId,
            "bridge must stamp OriginHostId with local host id to prevent re-forwarding");

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAll_NoFurtherForwards()
    {
        // Per ADR-0040 §4: DisposeAsync 应取消所有订阅, 后续发布的事件不再转发。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();
        await WaitAsync();
        await bridge.DisposeAsync();

        var path = new ItemPath { Provider = "fs", InternalPath = "/after-dispose" };
        bus.Publish(new ItemCreatedEvent { Path = path, Item = Item.File(path) });
        await WaitAsync();

        channel.SentMessages.Should().BeEmpty("no events should be forwarded after DisposeAsync");

        bus.Dispose();
    }

    [Fact]
    public async Task ForwardIfRemote_MultipleEvents_AllForwarded()
    {
        // Per ADR-0040 §9: 多个事件均应被转发。
        var bus = new InProcessEventBus();
        var channel = new StubIpcChannel();
        await using var bridge = new CrossProcessEventBridge(bus, channel, LocalHostId);

        await bridge.StartAsync();
        await WaitAsync();

        var path = new ItemPath { Provider = "fs", InternalPath = "/multi" };
        var item = Item.File(path);

        bus.Publish(new ItemCreatedEvent { Path = path, Item = item });
        bus.Publish(new ItemModifiedEvent { Path = path, Item = item });
        bus.Publish(new OperationStartedEvent
        {
            Operation = "copy",
            Sources = new[] { path },
            Destinations = new[] { path },
            TaskId = Guid.NewGuid(),
        });
        await WaitAsync();

        channel.SentMessages.Should().HaveCount(3,
            "three distinct IRemoteRoutableEvent publications should each be forwarded");
        channel.SentMessages.Should().AllBeAssignableTo<IpcEventMessage>(
            "all forwarded messages should be IpcEventMessage");

        await bridge.DisposeAsync();
        bus.Dispose();
    }

    /// <summary>等待后台 consumer 处理完。</summary>
    private static async Task WaitAsync()
    {
        // InProcessEventBus 的 consumer 在后台 Task 中处理, 给 50ms × 10 = 500ms 总等待。
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Stub IPC channel 用于测试 bridge 逻辑, 不真实建立 Named Pipe 连接。
    /// - SendAsync 捕获到 SentMessages 列表 (供断言)
    /// - InjectInbound 模拟对端发送消息 (推到 inbox channel, 触发 bridge 的 ListenAsync)
    /// - StartAsync / StopAsync 仅切换状态, 无 IO 操作
    /// </summary>
    private sealed class StubIpcChannel : IIpcChannel
    {
        private readonly System.Threading.Channels.Channel<IpcMessage> _inbox =
            System.Threading.Channels.Channel.CreateUnbounded<IpcMessage>();
        private int _state; // 0=idle, 1=running, 2=stopped

        public string ChannelName => "stub-channel";
        public bool IsConnected => _state == 1;
        public List<IpcMessage> SentMessages { get; } = new();

        public Task StartAsync(CancellationToken ct = default)
        {
            _state = 1;
            return Task.CompletedTask;
        }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            _state = 1;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            _state = 2;
            _inbox.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IpcMessage> ListenAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var msg in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return msg;
            }
        }

        public Task SendAsync(IpcMessage message, CancellationToken ct = default)
        {
            if (_state != 1)
                throw new InvalidOperationException("Stub channel is not connected.");
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _ = StopAsync();
            return ValueTask.CompletedTask;
        }

        /// <summary>模拟对端发送一条消息 (推到 inbox 供 ListenAsync 消费)。</summary>
        public void InjectInbound(IpcMessage message)
        {
            _inbox.Writer.TryWrite(message);
        }
    }
}
