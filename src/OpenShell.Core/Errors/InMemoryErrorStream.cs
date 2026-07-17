using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenShell.Logging;

namespace OpenShell.Errors;

/// <summary>
/// Default in-memory <see cref="IErrorStream"/> implementation. Per ADR-0026.
/// Thread-safe; bounded to <see cref="Capacity"/> records.
/// Per ADR-0031 §7: 若注入 <see cref="ILogStore"/>, 每条 ErrorRecord 同时以 LogLevel=Error 写入日志存储,
/// 便于通过 get-log 统一查询 (跨错误流/普通日志)。
/// </summary>
public sealed class InMemoryErrorStream : IErrorStream
{
    private readonly ConcurrentQueue<ErrorRecord> _queue = new();
    private readonly object _lock = new();
    private readonly ILogStore? _logStore;

    public int Capacity { get; init; } = 100;

    public ErrorRecord? LastError { get; private set; }

    public InMemoryErrorStream() : this(logStore: null) { }

    /// <summary>构造 InMemoryErrorStream, 可选注入 <see cref="ILogStore"/> 用于错误日志联动。</summary>
    /// <param name="logStore">日志存储; 非空时每次 Write 同步追加一条 LogLevel=Error 的日志。</param>
    public InMemoryErrorStream(ILogStore? logStore)
    {
        _logStore = logStore;
    }

    public IReadOnlyList<ErrorRecord> RecentErrors
    {
        get
        {
            lock (_lock)
            {
                return _queue.ToArray();
            }
        }
    }

    public event EventHandler<ErrorRecord>? ErrorWritten;

    public void Write(ErrorRecord error)
    {
        lock (_lock)
        {
            _queue.Enqueue(error);
            while (_queue.Count > Capacity && _queue.TryDequeue(out _)) { }
            LastError = error;
        }
        ErrorWritten?.Invoke(this, error);

        // 同步写入日志存储, 便于通过 get-log 查询错误。Per ADR-0031 §7.
        // 错误流写入已是异常路径, 不再额外 try/catch: logStore 异常不影响错误流本身。
        if (_logStore is not null)
        {
            _logStore.Append(new LogEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Error,
                Category = "ErrorStream",
                Message = $"[{error.Category}] {error.Operation ?? "unknown"}: {error.Message}",
                Exception = error.Exception,
                Scope = BuildErrorScope(error),
            });
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            LastError = null;
        }
    }

    /// <summary>把 ErrorRecord 的结构化字段提取为 LogEntry.Scope 字典, 便于程序化分析。</summary>
    private static IReadOnlyDictionary<string, object?>? BuildErrorScope(ErrorRecord error)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["errorId"] = error.ErrorId,
            ["category"] = error.Category.ToString(),
            ["phase"] = error.Phase.ToString(),
        };
        if (error.Operation is { } op) dict["operation"] = op;
        if (error.TargetPath is { } p) dict["targetPath"] = p.Display;
        if (error.Suggestion is { } s) dict["suggestion"] = s;
        return dict;
    }
}
