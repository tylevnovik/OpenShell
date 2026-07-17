namespace OpenShell.Events;

/// <summary>
/// 事件总线抽象。Per ADR-0040 §1.
/// 发布-订阅模型: <see cref="Publish{TEvent}"/> 同步返回 (非阻塞),
/// 订阅者在 InProcessEventBus 的后台 consumer 任务中串行处理。
/// 与 ADR-0021 命令 IPC 互补: 命令 IPC 是请求-响应, 事件总线是发布-订阅。
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// 发布事件。非阻塞: 写入内部 Channel 后立即返回, 订阅者异步处理。
    /// Per ADR-0040 §3: 单个订阅者异常不影响其他订阅者。
    /// </summary>
    /// <typeparam name="TEvent">事件类型, 必须实现 <see cref="IEvent"/>。</typeparam>
    /// <param name="event">事件实例。</param>
    void Publish<TEvent>(TEvent @event) where TEvent : IEvent;

    /// <summary>
    /// 订阅事件 (同步 handler)。返回 <see cref="IDisposable"/>, dispose 取消订阅。
    /// handler 内部抛异常被吞掉 (仅记录), 不影响其他订阅者。
    /// </summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IEvent;

    /// <summary>
    /// 订阅事件 (异步 handler)。返回 <see cref="IDisposable"/>, dispose 取消订阅。
    /// handler 内部抛异常被吞掉 (仅记录), 不影响其他订阅者。
    /// </summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IEvent;
}

/// <summary>
/// 事件标记接口。Per ADR-0040 §1.
/// 所有事件必须实现此接口, 携带全局唯一 EventId 与 Timestamp (UTC)。
/// 跨进程传播必须额外实现 <see cref="IRemoteRoutableEvent"/>。
/// </summary>
public interface IEvent
{
    /// <summary>事件全局唯一标识 (Guid)。</summary>
    Guid EventId { get; }

    /// <summary>事件时间戳 (UTC)。</summary>
    DateTimeOffset Timestamp { get; }
}
