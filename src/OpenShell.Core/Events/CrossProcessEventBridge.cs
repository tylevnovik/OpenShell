using System.Text.Json;
using OpenShell.Interop;

namespace OpenShell.Events;

/// <summary>
/// 跨进程事件桥。Per ADR-0040 §4.
/// 通过 <see cref="IIpcChannel"/> 把本地 <see cref="IRemoteRoutableEvent"/> 事件转发到对端进程,
/// 同时接收对端转发的 <see cref="IpcEventMessage"/> 并重新 Publish 到本地 <see cref="IEventBus"/>。
/// </summary>
/// <remarks>
/// 防回环机制 (ADR-0040 §4):
/// - 转发到对端前: ForwardIfRemote 检查 <see cref="IRemoteRoutableEvent.OriginHostId"/>,
///   若非 null 且 != 本地 host id, 说明事件来自对端, 跳过转发 (避免 A→B→A 循环)。
/// - 接收对端事件后重新 Publish 前: 通过 <c>with</c> 表达式将 OriginHostId 设置为本地 host id,
///   这样本端 bridge 的 ForwardIfRemote 检测到非本地 OriginHostId 时会跳过。
/// </remarks>
public sealed class CrossProcessEventBridge : IAsyncDisposable
{
    private readonly IEventBus _bus;
    private readonly IIpcChannel _channel;
    private readonly string _originHostId;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<IDisposable> _subscriptions = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    /// <summary>构造 CrossProcessEventBridge。</summary>
    /// <param name="bus">本地事件总线。</param>
    /// <param name="channel">IPC 通道 (Named Pipe / Unix Socket)。</param>
    /// <param name="originHostId">本端 host 唯一标识 (用于防回环), 通常用 <see cref="IIpcChannel.ChannelName"/> 或 Guid。</param>
    public CrossProcessEventBridge(IEventBus bus, IIpcChannel channel, string originHostId)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _originHostId = originHostId ?? throw new ArgumentNullException(nameof(originHostId));
        // 复用 IPC 序列化配置 (含 ItemPath / IItem 自定义 converter)。
        _jsonOptions = IpcMessageJsonContext.Options;

        // 订阅所有 IRemoteRoutableEvent 子类, 命中时尝试转发到对端。
        // 仅订阅具体事件类型, 不订阅 IRemoteRoutableEvent 本身 (InProcessEventBus 按 runtime type 索引)。
        _subscriptions.Add(_bus.Subscribe<ItemCreatedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<ItemDeletedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<ItemRenamedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<ItemModifiedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<ItemCopiedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<ItemMovedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<OperationStartedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<OperationProgressEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<OperationCompletedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<OperationFailedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<OperationCancelledEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<LocationChangedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<SelectionChangedEvent>(ForwardIfRemote));
        _subscriptions.Add(_bus.Subscribe<ConfigChangedEvent>(ForwardIfRemote));
    }

    /// <summary>
    /// 启动桥: 启动 IPC 通道并开始监听对端事件。
    /// 调用方负责确保通道生命周期 (server 模式 StartAsync 阻塞等待客户端连接)。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _channel.StartAsync(ct).ConfigureAwait(false);

        _listenTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _channel.ListenAsync(_cts.Token).ConfigureAwait(false))
                {
                    if (msg is not IpcEventMessage evtMsg) continue;

                    var evt = TryDeserializeEvent(evtMsg);
                    if (evt is null) continue;

                    // 标记 OriginHostId = 本地 host, 防止本端 ForwardIfRemote 再次转发回对端。
                    evt = WithOriginHostId(evt, _originHostId);
                    _bus.Publish(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // StopAsync 时正常退出。
            }
        }, _cts.Token);
    }

    /// <summary>
    /// 转发事件到对端 (若可路由且非来自对端)。
    /// Per ADR-0040 §4: 防回环 — OriginHostId 非 null 且 != 本地 host 的事件来自对端, 跳过。
    /// </summary>
    private async Task ForwardIfRemote<TEvent>(TEvent @event) where TEvent : IEvent
    {
        if (@event is not IRemoteRoutableEvent remote) return;

        // 防回环: 已被对端 bridge 标记过 OriginHostId 的事件不再转发回去。
        if (!string.IsNullOrEmpty(remote.OriginHostId)
            && !string.Equals(remote.OriginHostId, _originHostId, StringComparison.Ordinal))
        {
            return;
        }

        var msg = TrySerializeEvent(@event);
        if (msg is null) return;

        try
        {
            await _channel.SendAsync(msg, _cts?.Token ?? default).ConfigureAwait(false);
        }
        catch
        {
            // 对端断开 / 通道未就绪等异常静默 (事件不丢, 只是没转发)。
        }
    }

    private IpcEventMessage? TrySerializeEvent<TEvent>(TEvent @event) where TEvent : IEvent
    {
        try
        {
            var type = @event.GetType();
            var eventType = type.AssemblyQualifiedName ?? type.FullName;
            if (string.IsNullOrEmpty(eventType)) return null;

            var payload = JsonSerializer.Serialize(@event, type, _jsonOptions);
            return new IpcEventMessage(eventType, payload);
        }
        catch
        {
            // 序列化失败 (例如 IItem 不支持序列化) 静默丢弃, 不影响其他事件。
            return null;
        }
    }

    private IEvent? TryDeserializeEvent(IpcEventMessage msg)
    {
        if (string.IsNullOrEmpty(msg.EventType) || string.IsNullOrEmpty(msg.Payload))
            return null;

        try
        {
            var type = Type.GetType(msg.EventType);
            if (type is null) return null;
            return JsonSerializer.Deserialize(msg.Payload, type, _jsonOptions) as IEvent;
        }
        catch
        {
            // 反序列化失败 (类型不存在 / JSON 格式错误 / 版本不兼容) 静默丢弃。
            return null;
        }
    }

    /// <summary>
    /// 用 <c>with</c> 表达式将事件的 OriginHostId 设置为指定值。
    /// 由于 IRemoteRoutableEvent 是接口, 无法直接 with; 按具体类型 dispatch。
    /// </summary>
    private static IEvent WithOriginHostId(IEvent evt, string originHostId)
    {
        return evt switch
        {
            ItemCreatedEvent e => e with { OriginHostId = originHostId },
            ItemDeletedEvent e => e with { OriginHostId = originHostId },
            ItemRenamedEvent e => e with { OriginHostId = originHostId },
            ItemModifiedEvent e => e with { OriginHostId = originHostId },
            ItemCopiedEvent e => e with { OriginHostId = originHostId },
            ItemMovedEvent e => e with { OriginHostId = originHostId },
            OperationStartedEvent e => e with { OriginHostId = originHostId },
            OperationProgressEvent e => e with { OriginHostId = originHostId },
            OperationCompletedEvent e => e with { OriginHostId = originHostId },
            OperationFailedEvent e => e with { OriginHostId = originHostId },
            OperationCancelledEvent e => e with { OriginHostId = originHostId },
            LocationChangedEvent e => e with { OriginHostId = originHostId },
            SelectionChangedEvent e => e with { OriginHostId = originHostId },
            ConfigChangedEvent e => e with { OriginHostId = originHostId },
            _ => evt,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { /* best-effort */ }
        }
        _subscriptions.Clear();

        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_listenTask is not null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { /* best-effort */ }
            _listenTask = null;
        }
    }
}
