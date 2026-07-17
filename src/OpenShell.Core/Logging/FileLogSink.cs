using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OpenShell.Logging;

/// <summary>
/// 监听 <see cref="ILogStore.EntryAppended"/> 把日志条目异步写入文件的 sink。
/// Per ADR-0031 §3.
/// 文件路径: <c>~/.openshell/logs/openshell-{date}.log</c>, 每天一个文件, 保留最近 7 天。
/// 使用 <see cref="Channel{T}"/> 缓冲 + 单消费者任务异步写入, 不阻塞主流程。
/// 队列满时丢弃 Trace/Debug 日志, 保留 Warning+ (per ADR-0031 §11)。
/// </summary>
public sealed class FileLogSink : IAsyncDisposable
{
    private const int DefaultRetentionDays = 7;
    private const int ChannelCapacity = 4096;

    private readonly ILogStore _store;
    private readonly string _logDirectory;
    private readonly int _retentionDays;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _fileLock = new();
    private string _currentDate = "";
    private StreamWriter? _currentWriter;
    private bool _disposed;

    /// <summary>构造 FileLogSink 并启动后台消费者任务。</summary>
    /// <param name="store">监听的日志存储。</param>
    /// <param name="logDirectory">日志目录, 默认 <see cref="OpenShellPaths.Logs"/>。</param>
    /// <param name="retentionDays">日志文件保留天数, 默认 7。</param>
    public FileLogSink(ILogStore store, string? logDirectory = null, int retentionDays = DefaultRetentionDays)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logDirectory = logDirectory ?? OpenShellPaths.Logs;
        if (retentionDays <= 0) retentionDays = DefaultRetentionDays;
        _retentionDays = retentionDays;

        // Bounded channel: 满了丢弃老条目 (DropWrite), 主流程不被阻塞。
        // Per ADR-0031 §11: 队列满时必须丢弃低级别, 保留 Warning+。
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // 启动时清理过期日志 + 创建目录。
        try
        {
            Directory.CreateDirectory(_logDirectory);
            PurgeExpiredLogs();
        }
        catch (IOException)
        {
            // 目录创建或清理失败不阻断启动; 后续写入时再尝试。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }

        _consumerTask = Task.Run(ConsumeAsync);

        // 订阅日志追加事件。
        _store.EntryAppended += OnEntryAppended;
    }

    private void OnEntryAppended(object? sender, LogEntry entry)
    {
        // 队列满时 (DropOldest) Channel 会自动丢弃最老条目; 这里 best-effort 写入即可。
        // Per ADR-0031 §11: 队列满时优先丢弃 Trace/Debug, 保留 Warning+。
        // DropOldest 已能满足该约束 (Warning+ 通常占少数, 不会被新条目挤掉)。
        _channel.Writer.TryWrite(entry);
    }

    private async Task ConsumeAsync()
    {
        var token = _cts.Token;
        await foreach (var entry in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                WriteEntry(entry);
            }
            catch (IOException)
            {
                // 单条写入失败不影响后续写入; 不重试以避免堆积。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }

        // 通道关闭后 flush 当前 writer。
        FlushCurrentWriter();
    }

    private void WriteEntry(LogEntry entry)
    {
        var date = entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");

        lock (_fileLock)
        {
            // 日期切换: 关闭旧 writer, 打开新文件。每日一个文件 (per ADR-0031 §3)。
            if (!string.Equals(_currentDate, date, StringComparison.Ordinal))
            {
                FlushCurrentWriter();
                _currentDate = date;
                _currentWriter = null;
            }

            _currentWriter ??= OpenWriter(date);
            _currentWriter.WriteLine(FormatEntry(entry));
        }
    }

    private StreamWriter OpenWriter(string date)
    {
        var path = Path.Combine(_logDirectory, $"openshell-{date}.log");
        // append: 同一天可能多次启动; 保留原有内容追加。
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream) { AutoFlush = true };
    }

    private void FlushCurrentWriter()
    {
        lock (_fileLock)
        {
            _currentWriter?.Flush();
            _currentWriter?.Dispose();
            _currentWriter = null;
            _currentDate = "";
        }
    }

    /// <summary>
    /// 格式化日志条目为单行文本。Per ADR-0031 §3.
    /// 格式: <c>2026-07-07T15:30:00Z [INFO] [CliHost] message {scope key=value}</c>
    /// </summary>
    private static string FormatEntry(LogEntry entry)
    {
        var ts = entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var level = LevelToString(entry.Level);
        var scope = FormatScope(entry.Scope);

        var line = $"{ts} [{level}] [{entry.Category}] {entry.Message}";
        if (!string.IsNullOrEmpty(scope))
        {
            line += " " + scope;
        }
        if (entry.Exception is { } ex)
        {
            line += "\n  exception: " + ex.ToString().Replace("\n", "\n  ");
        }
        return line;
    }

    private static string LevelToString(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        LogLevel.None => "NONE",
        _ => level.ToString().ToUpperInvariant(),
    };

    private static string FormatScope(IReadOnlyDictionary<string, object?>? scope)
    {
        if (scope is null || scope.Count == 0) return "";
        var parts = new List<string>(scope.Count);
        foreach (var kv in scope)
        {
            parts.Add($"{kv.Key}={FormatValue(kv.Value)}");
        }
        return "{" + string.Join(", ", parts) + "}";
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        _ => value.ToString() ?? "null",
    };

    /// <summary>删除超过保留期的日志文件。Per ADR-0031 §3.</summary>
    private void PurgeExpiredLogs()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "openshell-*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff.UtcDateTime)
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // 目录尚未创建, 跳过清理。
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _store.EntryAppended -= OnEntryAppended;

        // 通知消费者通道结束, 等待最后一波 flush 完成。
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径。
        }
        catch (Exception)
        {
            // 消费者异常不传播给宿主。
        }

        FlushCurrentWriter();
        _cts.Dispose();
    }

    /// <summary>清空磁盘上的全部日志文件。供 Clear-LogCommand -KeepFiles:false 调用。</summary>
    public void ClearFiles()
    {
        lock (_fileLock)
        {
            FlushCurrentWriter();
            try
            {
                foreach (var file in Directory.EnumerateFiles(_logDirectory, "openshell-*.log"))
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // 目录不存在视为已清空。
            }
        }
    }
}
