using System.Text.Json.Serialization;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Interop;

// HostKind 定义于 OpenShell.Bridge.IHost (OpenShell 命名空间), 这里直接复用,
// 避免重复定义: HostKind { Cli, Gui }.

/// <summary>
/// IPC 端点命名工具。Per ADR-0021 §2.
/// Windows: <c>\\.\pipe\openshell-{sessionId}</c>; Linux/Mac: <c>/tmp/openshell-{sessionId}.sock</c>.
/// sessionId 优先取 <c>OPENSHELL_SESSION_ID</c> 环境变量 (子进程继承父进程),
/// 未设置时回退到 Windows Terminal Services session id 或当前 PID。
/// </summary>
public static class IpcEndpoints
{
    /// <summary>
    /// 获取当前进程的 IPC 端点名称。
    /// sessionId 来源: OPENSHELL_SESSION_ID 环境变量 → Process.SessionId (Windows) / PID (Unix)。
    /// </summary>
    public static string GetEndpointName()
    {
        var sessionId = ResolveSessionId();
        return GetEndpointName(sessionId);
    }

    /// <summary>
    /// 根据 sessionId 构造 IPC 端点名称。Per ADR-0021 §2.
    /// </summary>
    /// <param name="sessionId">会话标识 (来自 OPENSHELL_SESSION_ID 或进程级 session)。</param>
    public static string GetEndpointName(int sessionId)
    {
        if (OperatingSystem.IsWindows())
            return $@"\\.\pipe\openshell-{sessionId}";
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openshell-{sessionId}.sock");
    }

    private static int ResolveSessionId()
    {
        // 优先使用环境变量 (子进程继承父进程的 session id, 建立 IPC 通道)。
        var env = Environment.GetEnvironmentVariable("OPENSHELL_SESSION_ID");
        if (int.TryParse(env, out var envSession))
            return envSession;

        // 回退: Windows 用 Terminal Services session id, 其他平台用 PID。
        // 同一用户同一桌面会话的进程共享 Windows session id, 适合作为默认 IPC 命名空间。
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        return proc.SessionId;
    }
}

/// <summary>
/// IPC 通道抽象。Per ADR-0021.
/// GUI ↔ CLI 之间的跨进程通信通道, 支持启动/停止/监听/发送消息。
/// 实现端: Windows 用 Named Pipe, Linux/Mac 用 Unix Domain Socket。
/// </summary>
public interface IIpcChannel : IAsyncDisposable
{
    /// <summary>通道名称 (含 sessionId, 避免多用户冲突)。</summary>
    string ChannelName { get; }

    /// <summary>是否已连接对端 (服务端 accept 后或客户端 connect 后为 true)。</summary>
    bool IsConnected { get; }

    /// <summary>启动通道 (服务端模式: 创建 endpoint 并 accept 一个连接)。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>连接到服务端 (客户端模式: 连接到已存在的 endpoint)。</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>停止通道 (关闭 stream/socket, 断开连接, 可重入)。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>监听传入消息 (流式)。连接断开时自然结束 (yield break)。</summary>
    IAsyncEnumerable<IpcMessage> ListenAsync(CancellationToken ct = default);

    /// <summary>发送消息到对端。</summary>
    Task SendAsync(IpcMessage message, CancellationToken ct = default);
}

/// <summary>
/// IPC 消息基类。Per ADR-0021 §1.
/// 所有具体消息为 sealed record, 通过 <c>[JsonDerivedType]</c> 配置多态序列化。
/// <see cref="Type"/> 仅运行时使用, 序列化时由 JSON polymorphic discriminator 接管。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IpcHandshake), "handshake")]
[JsonDerivedType(typeof(IpcLocationChanged), "locationChanged")]
[JsonDerivedType(typeof(IpcSelectionChanged), "selectionChanged")]
[JsonDerivedType(typeof(IpcCommandRequest), "commandRequest")]
[JsonDerivedType(typeof(IpcCommandResponse), "commandResponse")]
[JsonDerivedType(typeof(IpcShowGridRequest), "showGridRequest")]
[JsonDerivedType(typeof(IpcShowGridResponse), "showGridResponse")]
[JsonDerivedType(typeof(IpcEventMessage), "eventMessage")]
[JsonDerivedType(typeof(IpcShutdown), "shutdown")]
public abstract record IpcMessage
{
    /// <summary>消息类型标识 (运行时使用, 序列化由 discriminator 接管)。</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>
/// 握手消息。Per ADR-0021 §8.
/// 连接建立后双方互发, ProtocolVersion 必须匹配, 不匹配 fail-fast。
/// </summary>
/// <param name="ProtocolVersion">协议版本 (当前 = 1)。</param>
/// <param name="SourceKind">发送方 host 类型 (Cli / Gui)。</param>
/// <param name="SessionId">会话标识 (同 session 的进程共享)。</param>
public sealed record IpcHandshake(int ProtocolVersion, HostKind SourceKind, Guid SessionId) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "handshake";
}

/// <summary>
/// 位置变更通知。Per ADR-0021 §6.
/// GUI tab 切换 → 推送此消息 → CLI 子进程更新 CurrentLocation。
/// </summary>
/// <param name="NewLocation">新的当前位置。</param>
public sealed record IpcLocationChanged(ItemPath NewLocation) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "locationChanged";
}

/// <summary>
/// 选中项变更通知。Per ADR-0021 §6.
/// GUI ListBox 选中 → 推送此消息 → CLI 子进程的 Selection 更新。
/// </summary>
/// <param name="Selected">当前选中的 IItem 列表。</param>
public sealed record IpcSelectionChanged(IReadOnlyList<IItem> Selected) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "selectionChanged";
}

/// <summary>
/// 命令执行请求。Per ADR-0021 §1.
/// GUI → CLI: 请求 CLI 子进程执行命令行, 结果通过 <see cref="IpcCommandResponse"/> 返回。
/// </summary>
/// <param name="CommandLine">要执行的命令行 (含参数)。</param>
/// <param name="WorkingDirectory">工作目录 (ItemPath, provider-namespaced)。</param>
/// <param name="RequestId">请求标识, 用于配对响应。</param>
public sealed record IpcCommandRequest(string CommandLine, ItemPath WorkingDirectory, Guid RequestId) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "commandRequest";
}

/// <summary>
/// 命令执行响应。Per ADR-0021 §1.
/// CLI → GUI: 返回命令执行结果。
/// </summary>
/// <param name="RequestId">对应的请求标识。</param>
/// <param name="ExitCode">退出码 (0 = 正常, 非 0 = 异常)。</param>
/// <param name="Error">错误信息 (失败时)。</param>
public sealed record IpcCommandResponse(Guid RequestId, int ExitCode, string? Error) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "commandResponse";
}

/// <summary>
/// Grid 视图展示请求。Per ADR-0021 §10.
/// CLI → GUI: 请求 GUI 弹出 grid view 展示对象流, 用户选中后通过 <see cref="IpcShowGridResponse"/> 返回。
/// </summary>
/// <param name="Items">要展示的 IItem 列表 (全部序列化, 上限 10000 项)。</param>
/// <param name="RequestId">请求标识, 用于配对响应。</param>
public sealed record IpcShowGridRequest(IReadOnlyList<IItem> Items, Guid RequestId) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "showGridRequest";
}

/// <summary>
/// Grid 视图展示响应。Per ADR-0021 §10.
/// GUI → CLI: 返回用户选中的项 (用户取消时 SelectedItems 为 null)。
/// </summary>
/// <param name="RequestId">对应的请求标识。</param>
/// <param name="SelectedItems">用户选中的 IItem 列表 (取消时为 null)。</param>
public sealed record IpcShowGridResponse(Guid RequestId, IReadOnlyList<IItem>? SelectedItems) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "showGridResponse";
}

/// <summary>
/// 关闭通知。Per ADR-0021 §7.
/// 用户主动关闭子进程窗口时发送给父进程, 通知对方清理资源。
/// </summary>
public sealed record IpcShutdown : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "shutdown";
}

/// <summary>
/// 事件转发消息。Per ADR-0040 §4 (CrossProcessEventBridge).
/// <see cref="EventType"/> 为事件类型的 assembly-qualified name, 接收方用 <see cref="Type.GetType(string)"/> 还原;
/// <see cref="Payload"/> 为事件实例的 JSON 序列化字符串 (使用 IpcMessageJsonContext.Options 处理 ItemPath / IItem)。
/// </summary>
/// <param name="EventType">事件类型 assembly-qualified name (用于 Type.GetType)。</param>
/// <param name="Payload">事件 JSON 序列化字符串。</param>
public sealed record IpcEventMessage(string EventType, string Payload) : IpcMessage
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "eventMessage";
}
