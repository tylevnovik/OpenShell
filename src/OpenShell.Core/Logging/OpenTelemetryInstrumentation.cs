using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenShell.Logging;

/// <summary>
/// OpenTelemetry 检测入口: 集中管理 <see cref="ActivitySource"/> 与 <see cref="Meter"/>。
/// Per ADR-0031 §5 (Tracing) + §6 (Metrics)。
/// 所有命令/Provider 的 span 与指标都从此处创建, 由 <c>ObservabilityExtensions.AddOpenShellObservability</c>
/// 统一注册到 OpenTelemetry SDK + OTLP exporter。
/// </summary>
public static class OpenTelemetryInstrumentation
{
    /// <summary>ActivitySource 名称, 与 OpenTelemetry SDK <c>AddSource</c> 对应。值为 "OpenShell"。</summary>
    public const string ActivitySourceName = "OpenShell";

    /// <summary>Meter 名称, 与 OpenTelemetry SDK <c>AddMeter</c> 对应。值为 "OpenShell"。</summary>
    public const string MeterName = "OpenShell";

    private const string ServiceVersion = "0.1.0";

    /// <summary>
    /// 全局 <see cref="ActivitySource"/>, 用于创建命令执行 / Provider 调用的 span。
    /// Per ADR-0031 §5: 每条命令执行是一个 root span, 含子 span (如 cp 内的文件复制)。
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, ServiceVersion);

    /// <summary>
    /// 全局 <see cref="Meter"/>, 用于创建计数器 / 直方图 / 仪表。
    /// Per ADR-0031 §6。
    /// </summary>
    public static Meter Meter { get; } = new(MeterName, ServiceVersion);

    /// <summary>
    /// 命令执行次数计数器。Per ADR-0031 §6: <c>openshell_commands_total{command, status}</c>。
    /// 标签: command (命令全名), status (ok/error)。
    /// </summary>
    public static Counter<long> CommandsExecuted { get; } =
        Meter.CreateCounter<long>("openshell_commands_executed_total");

    /// <summary>
    /// 命令执行延迟直方图 (秒)。Per ADR-0031 §6: <c>openshell_command_duration_ms{command}</c>。
    /// 单位: 秒 (s)。标签: command (命令全名)。
    /// </summary>
    public static Histogram<double> CommandDuration { get; } =
        Meter.CreateHistogram<double>("openshell_command_duration_seconds", unit: "s");

    /// <summary>
    /// 管道段处理次数计数器。Per ADR-0031 §6 (扩展): 跟踪 pipeline 中各段 (filter/sort/select/format) 的处理次数。
    /// 标签: segment (段类型)。
    /// </summary>
    public static Counter<long> PipelineSegmentsProcessed { get; } =
        Meter.CreateCounter<long>("openshell_pipeline_segments_total");
}
