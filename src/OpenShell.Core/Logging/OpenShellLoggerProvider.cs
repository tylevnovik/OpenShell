using Microsoft.Extensions.Logging;

namespace OpenShell.Logging;

/// <summary>
/// 将 <c>Microsoft.Extensions.Logging.ILogger</c> 接入 <see cref="ILogStore"/> 的 provider。
/// Per ADR-0031 §1.
/// 配置项: <see cref="MinLevel"/> (默认 Information) + <see cref="Categories"/> (白名单, 空=全部)。
/// 通过 <c>ISupportExternalScope</c> 接收 host 的 scope provider, 用于收集 BeginScope 字典。
/// </summary>
public sealed class OpenShellLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ILogStore _store;
    private LogLevel _minLevel = LogLevel.Information;
    private HashSet<string>? _categories; // null = 全部接受 (无白名单)
    private IExternalScopeProvider? _scopeProvider;

    /// <summary>构造 OpenShellLoggerProvider。</summary>
    /// <param name="store">日志存储后端, 由 logger 写入。</param>
    public OpenShellLoggerProvider(ILogStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>最低日志级别 (含)。默认 <see cref="LogLevel.Information"/>。</summary>
    public LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    /// <summary>Logger 类别白名单 (大小写不敏感)。null 或空 = 接受全部类别。</summary>
    public IReadOnlyCollection<string>? Categories
    {
        get => _categories;
        set => _categories = value is null || value.Count == 0
            ? null
            : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new OpenShellLogger(this, categoryName);

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    /// <inheritdoc />
    public void Dispose()
    {
        // 无需释放 store; store 由 DI 容器托管生命周期。
    }

    internal bool IsEnabled(LogLevel level, string category)
    {
        if (level < _minLevel) return false;
        if (_categories is null) return true;
        return _categories.Contains(category);
    }

    internal void Append(LogEntry entry) => _store.Append(entry);

    internal IExternalScopeProvider? ScopeProvider => _scopeProvider;
}

/// <summary>
/// <see cref="OpenShellLoggerProvider"/> 内部使用的 logger 实现。Per ADR-0031 §1.
/// 把 <c>ILogger.Log&lt;TState&gt;</c> 调用转换为 <see cref="LogEntry"/> 并写入 store。
/// 通过 <c>IExternalScopeProvider</c> 收集 BeginScope 链上的字典字段。
/// </summary>
internal sealed class OpenShellLogger : ILogger
{
    private readonly OpenShellLoggerProvider _provider;
    private readonly string _category;

    public OpenShellLogger(OpenShellLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        // 通过 external scope provider 把 state 推到 host 维护的 scope 栈。
        // 若 host 未提供 (例如测试场景), 返回 null 表示 scope 未生效。
        return _provider.ScopeProvider?.Push(state);
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel, _category);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        // 格式化消息。formatter 由 ILogger 扩展方法 (LogInformation 等) 自动传入。
        var message = formatter?.Invoke(state, exception) ?? string.Empty;

        // 收集 scope 字段: 遍历 scope 链上所有 KeyValuePair / IDictionary<string, object?>。
        // Per ADR-0031 §2: 结构化字段记录到 LogEntry.Scope, 便于程序化分析。
        var accumulator = new ScopeAccumulator();

        // 先从当前 state 提取 KeyValuePair 字段 (结构化日志参数, 如 LogInformation("copy {src}", path))。
        if (state is IEnumerable<KeyValuePair<string, object?>> stateKvps)
        {
            foreach (var kv in stateKvps)
            {
                accumulator.Fields ??= new Dictionary<string, object?>();
                accumulator.Fields[kv.Key] = kv.Value;
            }
        }

        // 再遍历 scope 链上每一层 scope, 合并字典字段 (后入优先)。
        var scopeProvider = _provider.ScopeProvider;
        scopeProvider?.ForEachScope(ExtractScope, accumulator);

        var entry = new LogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Level = logLevel,
            Category = _category,
            Message = message,
            Exception = exception,
            Scope = accumulator.Fields,
        };

        _provider.Append(entry);
    }

    /// <summary>ForEachScope 回调使用的状态对象: 累积结构化字段。</summary>
    private sealed class ScopeAccumulator
    {
        public Dictionary<string, object?>? Fields;
    }

    private static void ExtractScope(object? scopeState, ScopeAccumulator acc)
    {
        switch (scopeState)
        {
            case IEnumerable<KeyValuePair<string, object?>> kvps:
                acc.Fields ??= new Dictionary<string, object?>();
                foreach (var kv in kvps)
                {
                    acc.Fields[kv.Key] = kv.Value;
                }
                break;

            case System.Collections.IDictionary dict:
                acc.Fields ??= new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    if (kv.Key is string s)
                    {
                        acc.Fields[s] = kv.Value;
                    }
                }
                break;

            default:
                // 非字典 scope (如 string scope) 不提取字段, 但保留为 "scope" 字符串以便排查。
                if (scopeState is not null)
                {
                    acc.Fields ??= new Dictionary<string, object?>();
                    acc.Fields["scope"] = scopeState;
                }
                break;
        }
    }
}
