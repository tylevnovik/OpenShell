using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace OpenShell.Logging;

/// <summary>
/// 可观测性配置选项。Per ADR-0031 §5-9, §10.
/// 控制 Serilog 日志、OpenTelemetry traces/metrics、OTLP 导出的开关。
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>是否启用 OpenTelemetry tracing (ActivitySource)。默认 true。Per ADR-0031 §5。</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>是否启用 OpenTelemetry metrics (Meter)。默认 true。Per ADR-0031 §6。</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// OTLP exporter 端点 (gRPC)。例如 "http://localhost:4317"。
    /// 为 null 时不配置 OTLP 导出 (仅注册 ActivitySource / Meter, 不发送数据)。
    /// Per ADR-0031 §10: 默认关闭, 用户显式启用。
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// 是否启用控制台输出 (开发调试用)。默认 false。
    /// 影响: Serilog console sink (日志输出到控制台)。
    /// </summary>
    public bool EnableConsoleExport { get; set; } = false;

    /// <summary>最低日志级别。默认 Information。Per ADR-0031 §4。</summary>
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// DI 扩展方法: 注册 M3+ 可观测性栈 (Serilog + OpenTelemetry)。Per ADR-0031 §5-9.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// 注册 Serilog 结构化日志 + OpenTelemetry traces/metrics + OTLP exporter + 运行时指标。
    /// 不影响现有 M1 日志 (OpenShellLoggerProvider / FileLogSink), 两者并行运行。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="options">可观测性配置。</param>
    /// <returns>原 <paramref name="services"/> 引用, 便于链式调用。</returns>
    public static IServiceCollection AddOpenShellObservability(
        this IServiceCollection services,
        ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // --- Serilog 结构化日志 (JSON Lines 格式, 每日轮转, 保留 7 天)。Per ADR-0031 §2-3。
        var serilogConfig = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(options.MinimumLogLevel))
            .Enrich.FromLogContext();

        if (options.EnableConsoleExport)
        {
            serilogConfig.WriteTo.Console();
        }

        // JSON Lines 文件: ~/.openshell/logs/openshell-structured-{date}.jsonl
        // 与 M1 FileLogSink 的 openshell-{date}.log 分离, 避免冲突。
        Directory.CreateDirectory(OpenShellPaths.Logs);
        serilogConfig.WriteTo.File(
            path: Path.Combine(OpenShellPaths.Logs, "openshell-structured-.jsonl"),
            formatter: new JsonFormatter(),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7);

        var serilogLogger = serilogConfig.CreateLogger();
        services.AddSingleton<Serilog.ILogger>(serilogLogger);

        // --- OpenTelemetry: traces + metrics + OTLP exporter。Per ADR-0031 §5-6, §10。
        var otelBuilder = services.AddOpenTelemetry();

        if (options.EnableTracing)
        {
            otelBuilder.WithTracing(tp =>
            {
                tp.AddSource(OpenTelemetryInstrumentation.ActivitySourceName);

                if (!string.IsNullOrEmpty(options.OtlpEndpoint))
                {
                    tp.AddOtlpExporter(o => o.Endpoint = new Uri(options.OtlpEndpoint!));
                }
            });
        }

        if (options.EnableMetrics)
        {
            otelBuilder.WithMetrics(mp =>
            {
                mp.AddMeter(OpenTelemetryInstrumentation.MeterName);
                // 运行时指标: GC / ThreadPool / 内存 / CPU。Per ADR-0031 §6。
                mp.AddRuntimeInstrumentation();

                if (!string.IsNullOrEmpty(options.OtlpEndpoint))
                {
                    mp.AddOtlpExporter(o => o.Endpoint = new Uri(options.OtlpEndpoint!));
                }
            });
        }

        return services;
    }

    /// <summary>
    /// 把 <see cref="LogLevel"/> 映射到 Serilog 的 <see cref="LogEventLevel"/>。
    /// </summary>
    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        LogLevel.None => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
