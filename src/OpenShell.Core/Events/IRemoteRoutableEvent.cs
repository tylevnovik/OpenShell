namespace OpenShell.Events;

/// <summary>
/// 标记事件可跨进程传播。Per ADR-0040 §3.
/// 实现此接口的事件会被 <see cref="CrossProcessEventBridge"/> 转发到对端进程。
/// 未实现此接口的事件仅在本地进程内传播 (例如 ErrorOccurredEvent / SessionStartedEvent)。
/// </summary>
/// <remarks>
/// 防回环机制: bridge 在转发事件到对端时, 接收方在重新 Publish 前会通过 <c>with</c> 表达式
/// 将 <see cref="OriginHostId"/> 设置为本地 host id; ForwardIfRemote 检测到
/// OriginHostId 非 null 且不等于本地 host id 时跳过转发, 避免事件在两端无限循环。
/// </remarks>
public interface IRemoteRoutableEvent : IEvent
{
    /// <summary>目标 session id, null = 广播给所有 session。</summary>
    string? TargetSession { get; }

    /// <summary>
    /// 事件来源 host id (用于防回环)。null = 本地新发布的事件;
    /// 非 null = 已被某个 bridge 转发过, 接收方 bridge 不再转发回去。
    /// </summary>
    string? OriginHostId { get; }
}
