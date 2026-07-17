using Microsoft.Extensions.Logging;

namespace OpenShell.Logging;

/// <summary>
/// 结构化日志存储抽象。Per ADR-0031 §2.
/// 提供 in-memory 查询 + 事件订阅能力, 由 <see cref="OpenShellLoggerProvider"/> 写入。
/// 文件持久化由 <c>FileLogSink</c> 监听 <see cref="EntryAppended"/> 异步完成。
/// </summary>
public interface ILogStore
{
    /// <summary>追加一条日志条目。线程安全; 实现可使用环形缓冲区。</summary>
    void Append(LogEntry entry);

    /// <summary>获取最近 N 条日志 (按时间顺序, 最近的在末尾)。</summary>
    IReadOnlyList<LogEntry> Recent(int count = 100);

    /// <summary>按条件过滤日志 (按 MinLevel / Category / 时间范围 / 消息子串)。</summary>
    IReadOnlyList<LogEntry> Filter(LogFilter filter);

    /// <summary>清空内存中的全部日志条目 (不影响已写入磁盘的文件)。</summary>
    void Clear();

    /// <summary>新日志条目追加时触发。FileLogSink 订阅此事件异步落盘。</summary>
    event EventHandler<LogEntry>? EntryAppended;
}

/// <summary>
/// 单条结构化日志条目。Per ADR-0031 §2.
/// 字段语义对齐 <c>Microsoft.Extensions.Logging</c>: Level / Category / Message / Exception / Scope。
/// </summary>
public sealed record LogEntry
{
    /// <summary>条目唯一标识。</summary>
    public required Guid Id { get; init; }

    /// <summary>日志时间戳 (UTC)。</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>日志级别 (Trace/Debug/Information/Warning/Error/Critical)。</summary>
    public required LogLevel Level { get; init; }

    /// <summary>Logger 类别 (通常 <c>typeof(T).Name</c> 或命名空间)。</summary>
    public required string Category { get; init; }

    /// <summary>已格式化的日志消息。</summary>
    public required string Message { get; init; }

    /// <summary>关联的异常 (可为 null)。</summary>
    public Exception? Exception { get; init; }

    /// <summary>BeginScope 收集的结构化字段 (key=value 字典), 可为 null。</summary>
    public IReadOnlyDictionary<string, object?>? Scope { get; init; }
}

/// <summary>
/// 日志查询过滤器。Per ADR-0031 §12.
/// 所有条件均为可选; null 表示不限制该字段。
/// </summary>
public sealed record LogFilter
{
    /// <summary>最低日志级别 (含)。null = 不限制。</summary>
    public LogLevel? MinLevel { get; init; }

    /// <summary>Logger 类别精确匹配 (大小写不敏感)。null = 不限制。</summary>
    public string? Category { get; init; }

    /// <summary>起始时间 (含)。null = 不限制。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>截止时间 (含)。null = 不限制。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>消息子串匹配 (大小写不敏感)。null = 不限制。</summary>
    public string? MessageContains { get; init; }
}
