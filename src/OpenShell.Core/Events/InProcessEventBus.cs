using System.Threading.Channels;

namespace OpenShell.Events;

/// <summary>
/// 进程内事件总线默认实现。Per ADR-0040 §3.
/// 用 <see cref="System.Threading.Channels.Channel{T}"/> 做内部队列,
/// 单消费者任务串行处理所有订阅者, 保证事件顺序 (Per ADR-0040 §9: 同 Provider 内事件有序)。
/// </summary>
/// <remarks>
/// 线程安全: <see cref="Publish{TEvent}"/> 可多线程并发调用 (Channel 是线程安全的);
/// <see cref="Subscribe{TEvent}(Action{TEvent})"/> / <see cref="Subscribe{TEvent}(Func{TEvent, Task})"/> 用锁保护 handlers 字典。
/// 异常隔离: 单个 handler 抛异常被 try/catch 吞掉, 不影响其他 handler 与后续事件。
/// </remarks>
public sealed class InProcessEventBus : IEventBus, IDisposable
{
    // Unbounded Channel: 事件不会被丢弃 (与 ADR-0040 §6 BoundedChannelFullMode.DropOldest 不同,
    // 这里是进程内默认实现, 假设订阅者跟得上; 高频场景应由发布者自行聚合)。
    private readonly Channel<IEvent> _queue = Channel.CreateUnbounded<IEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,   // 单消费者
        SingleWriter = false,  // 多线程可并发 Publish
    });

    // 按 event runtime type 索引的 handler 列表。Delegate 存储包装后的 Action<IEvent> / Func<IEvent, Task>。
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();
    private readonly Task _consumer;
    private readonly CancellationTokenSource _cts = new();
    private int _disposeState;

    public InProcessEventBus()
    {
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <inheritdoc />
    public void Publish<TEvent>(TEvent @event) where TEvent : IEvent
    {
        // 非阻塞: 写入 channel 后立即返回。Channel 已关闭时静默丢弃 (dispose 后)。
        _queue.Writer.TryWrite(@event);
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var eventType = typeof(TEvent);

        // 包装为 Action<IEvent>: 内部做 runtime type 检查 (ConsumeAsync 按 evt.GetType() 索引, 类型应匹配)。
        Action<IEvent> wrapper = e =>
        {
            if (e is TEvent typed)
                handler(typed);
        };

        lock (_lock)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
            {
                list = new List<Delegate>();
                _handlers[eventType] = list;
            }
            list.Add(wrapper);
        }

        return new Subscription(() => RemoveHandler(eventType, wrapper));
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var eventType = typeof(TEvent);

        Func<IEvent, Task> wrapper = e =>
        {
            if (e is TEvent typed)
                return handler(typed);
            return Task.CompletedTask;
        };

        lock (_lock)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
            {
                list = new List<Delegate>();
                _handlers[eventType] = list;
            }
            list.Add(wrapper);
        }

        return new Subscription(() => RemoveHandler(eventType, wrapper));
    }

    private void RemoveHandler(Type eventType, Delegate wrapper)
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(eventType, out var list))
            {
                list.Remove(wrapper);
                if (list.Count == 0)
                    _handlers.Remove(eventType);
            }
        }
    }

    /// <summary>
    /// 单消费者任务: 从 channel 读取事件, 按事件 runtime type 查找 handlers 串行调用。
    /// Per ADR-0040 §9: 同一 type 的事件保持发布顺序。
    /// </summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                List<Delegate> handlersSnapshot;
                lock (_lock)
                {
                    if (!_handlers.TryGetValue(evt.GetType(), out var handlers))
                        continue;
                    // 拷贝快照避免迭代期间被 Subscribe/Dispose 修改。
                    handlersSnapshot = handlers.ToList();
                }

                foreach (var h in handlersSnapshot)
                {
                    try
                    {
                        if (h is Action<IEvent> sync)
                            sync(evt);
                        else if (h is Func<IEvent, Task> asyncHandler)
                            await asyncHandler(evt).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 单个 handler 失败不影响其他 handler 与后续事件 (ADR-0040 §3 异常隔离)。
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose 时正常退出。
        }
        catch (ChannelClosedException)
        {
            // Channel 被关闭时正常退出。
        }
    }

    /// <summary>停止消费者任务并释放资源。可重入。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _consumer.Wait();
        }
        catch
        {
            // AggregateException: 内部 OperationCanceledException, 已预期。
        }
        _cts.Dispose();
    }

    /// <summary>取消订阅句柄。Dispose 时从 handlers 字典移除对应 wrapper。</summary>
    private sealed class Subscription : IDisposable
    {
        private Action? _removeAction;
        private bool _disposed;

        public Subscription(Action removeAction) => _removeAction = removeAction;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _removeAction?.Invoke(); } finally { _removeAction = null; }
        }
    }
}
