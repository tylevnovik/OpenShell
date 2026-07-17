#nullable enable
// ADR-0059 §1: 远程会话抽象。
// 设计：
//   1. IPSSession 是所有传输层 (SSH / 未来 WinRM) 的统一抽象。
//   2. InvokeAsync 接收已序列化的脚本块载荷，返回远端最终结果值。
//   3. IAsyncDisposable 确保会话关闭时释放底层资源 (ssh 子进程等)。

namespace OpenShell.Remoting;

/// <summary>ADR-0059 §1: 远程 PSSession 抽象接口。</summary>
public interface IPSSession : IAsyncDisposable
{
    /// <summary>会话 ID (由 PSSessionManager 分配的全局唯一递增整数)。</summary>
    int Id { get; }

    /// <summary>目标主机名 (user@host 或 host)。</summary>
    string ComputerName { get; }

    /// <summary>传输层标识 ("SSH"; 未来可扩展 "WinRM")。</summary>
    string Transport { get; }

    /// <summary>会话当前状态。</summary>
    PSSessionState State { get; }

    /// <summary>会话友好名 (可选, 由 New-PSSession -Name 指定)。</summary>
    string? Name { get; }

    /// <summary>
    /// 在会话上执行已序列化的脚本块。Per ADR-0059 §2/§6.
    /// </summary>
    /// <param name="payload">序列化载荷 (源文本 + $using 捕获 + 位置参数)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>远端最终结果值 (已反序列化)。</returns>
    Task<object?> InvokeAsync(SerializedScriptBlock payload, CancellationToken ct = default);
}

/// <summary>ADR-0059 §1: 会话状态。</summary>
public enum PSSessionState
{
    /// <summary>未连接 / 已断开。</summary>
    Disconnected,

    /// <summary>已打开, 可执行命令。</summary>
    Opened,

    /// <summary>已关闭, 不可复用。</summary>
    Closed,

    /// <summary>故障 (ssh 进程崩溃 / 网络中断)。</summary>
    Faulted,
}

/// <summary>ADR-0059 §3: 创建会话的选项。</summary>
public sealed record PSSessionOptions
{
    /// <summary>目标主机 (user@host 或 host)。必填。</summary>
    public required string HostName { get; init; }

    /// <summary>会话友好名。可选。</summary>
    public string? Name { get; init; }

    /// <summary>远端 OpenShell 可执行文件路径 (默认 "openshell", 依赖远端 PATH)。</summary>
    public string RemoteOpenShellPath { get; init; } = "openshell";

    /// <summary>ssh 端口 (默认 22)。</summary>
    public int Port { get; init; } = 22;
}

/// <summary>ADR-0059 §4: 序列化的脚本块载荷。跨主机传递的不可变记录。</summary>
public sealed record SerializedScriptBlock(
    string Script,
    IReadOnlyDictionary<string, object?> UsingValues,
    IReadOnlyList<object?> Args);
