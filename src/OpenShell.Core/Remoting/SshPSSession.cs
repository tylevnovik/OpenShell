#nullable enable
// ADR-0059 §2: SSH 传输层 PSSession 实现。
// 设计：
//   1. 启动 ssh user@host openshell --no-interactive --receive-serialized 子进程。
//   2. stdin/stdout 走 JSON-Lines 协议 (每行一个 JSON 对象)。
//   3. InvokeAsync 发送 invoke 消息，读取 result/error 响应。
//   4. 进程退出 / 读取异常时标记 Faulted。

using System.Diagnostics;
using System.Text.Json;
using OpenShell.Runtime;

namespace OpenShell.Remoting;

/// <summary>ADR-0059 §2: 基于 ssh 子进程的 PSSession 实现。</summary>
public sealed class SshPSSession : IPSSession
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public int Id { get; }
    public string ComputerName { get; }
    public string Transport => "SSH";
    public string? Name { get; }
    public PSSessionState State { get; private set; }

    internal SshPSSession(int id, PSSessionOptions options)
    {
        Id = id;
        ComputerName = options.HostName;
        Name = options.Name;

        // 构造 ssh 命令行: ssh [-p port] user@host openshell --no-interactive --receive-serialized
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (options.Port != 22)
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(options.Port.ToString());
        }
        // 禁用交互式密码提示 (强制密钥认证); 严格主机密钥检查保持默认。
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add(options.HostName);
        psi.ArgumentList.Add(options.RemoteOpenShellPath);
        psi.ArgumentList.Add("--no-interactive");
        psi.ArgumentList.Add("--receive-serialized");

        _process = new Process { StartInfo = psi };
        _process.Start();

        // UTF-8 编码确保跨平台一致。
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };
        _stdout = new StreamReader(_process.StandardOutput.BaseStream, System.Text.Encoding.UTF8);

        State = PSSessionState.Opened;
    }

    /// <summary>
    /// 在会话上执行序列化脚本块。Per ADR-0059 §2/§6.
    /// 发送 invoke JSON 行，读取 result/error 响应。
    /// </summary>
    public async Task<object?> InvokeAsync(SerializedScriptBlock payload, CancellationToken ct = default)
    {
        if (State != PSSessionState.Opened)
            throw new InvalidOperationException($"PSSession {Id} is not opened (state={State}).");

        var invokeMsg = new InvokeMessage
        {
            Script = payload.Script,
            Using = payload.UsingValues,
            Args = payload.Args,
        };
        var json = JsonSerializer.Serialize(invokeMsg, JsonOptions);

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SshPSSession));
            _stdin.WriteLine(json);
        }

        // 读取响应行 (同步阻塞读行, ct 不直接支持 StreamReader.ReadLineAsync 取消)。
        var line = await _stdout.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null)
        {
            State = PSSessionState.Faulted;
            throw new InvalidOperationException(
                $"PSSession {Id}: remote closed connection (ssh process exited).");
        }

        var response = JsonSerializer.Deserialize<ResponseMessage>(line, JsonOptions);
        if (response is null)
            throw new InvalidOperationException($"PSSession {Id}: invalid response from remote.");

        if (response.Kind == "error")
            throw new RemoteExecutionException(
                response.Message ?? "(no message)",
                response.Category ?? "RemoteError");

        // result 消息: 返回 value 字段。
        return response.Value;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        State = PSSessionState.Closed;
        try { _stdin.Close(); } catch { }
        try { _stdout.Close(); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
        _process.Dispose();
    }

    // =========================================================================
    // JSON-Lines 协议消息 DTO
    // =========================================================================

    private sealed class InvokeMessage
    {
        public string Kind => "invoke";
        public string Script { get; set; } = "";
        public IReadOnlyDictionary<string, object?>? Using { get; set; }
        public IReadOnlyList<object?>? Args { get; set; }
    }

    private sealed class ResponseMessage
    {
        public string? Kind { get; set; }      // "result" | "error"
        public object? Value { get; set; }      // result.value
        public string? Message { get; set; }    // error.message
        public string? Category { get; set; }   // error.category
    }
}

/// <summary>远端执行异常。Per ADR-0059 §2 error 消息。</summary>
public sealed class RemoteExecutionException : Exception
{
    public string RemoteErrorCategory { get; }

    public RemoteExecutionException(string message, string category)
        : base(message)
    {
        RemoteErrorCategory = category;
    }
}
