# ADR-0031: 日志与可观测性

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: 跨阶段（M1 起步，M3+ 完整）
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0026 (错误模型), ADR-0022 (配置), ADR-0014 (Bridge)

## Context

OpenShell 是框架底座，需可观测性：

1. **结构化日志**：日志含上下文（命令名、用户、路径、操作 ID）
2. **日志级别**：Trace / Debug / Info / Warning / Error
3. **日志输出**：文件轮转 + 控制台（CLI 调试模式）
4. **Tracing**：每条命令 / 每个操作是一个 span，含子 span（如 cp 内的文件复制）
5. **Metrics**：操作计数、延迟、Provider 调用次数、缓存命中率
6. **诊断**：用户报问题时能导出诊断包
7. **隐私**：日志不能含凭据 / 敏感路径（脱敏）
8. **跨进程**：GUI 与 CLI 子进程的日志统一（ADR-0021 IPC）
9. **性能影响**：日志不能显著影响主流程

参考：
- .NET `Microsoft.Extensions.Logging` + Serilog
- OpenTelemetry .NET SDK
- PowerShell 的 `$PSHostPrivateData` + 事件日志

## Decision

### 1. 日志栈

```
┌──────────────────────────────────────┐
│ Application Code                    │
│ (ILogger<T> / ActivitySource)        │
└────────┬──────────────┬──────────────┘
         │              │
┌────────▼─────┐  ┌─────▼─────────────┐
│ Serilog      │  │ OpenTelemetry     │
│ (logs)       │  │ (traces, metrics) │
└────────┬─────┘  └─────┬─────────────┘
         │              │
    ┌────▼──────┐  ┌────▼──────┐
    │ File      │  │ OTLP      │
    │ Rotating  │  │ Exporter  │
    │           │  │ (optional)│
    └───────────┘  └───────────┘
```

### 2. 结构化日志

每条日志字段：

```csharp
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,                  // Trace/Debug/Info/Warning/Error
    string Category,               // 命名空间
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    Exception? Exception,
    Guid? TraceId,
    Guid? SpanId);
```

写入格式：JSON Lines（便于程序化分析）：

```json
{"ts":"2026-07-07T15:30:00.123Z","level":"info","cat":"OpenShell.Commands.Builtins","msg":"copied item","fields":{"src":"fs::C:/a.txt","dst":"fs::C:/b.txt","bytes":1024},"traceId":"...","spanId":"..."}
```

### 3. 文件轮转

`~/.openshell/logs/openshell-{date}.jsonl`：

- 每日一个文件
- 单文件 > 50MB 强制轮转
- 保留最近 7 天
- 启动时清理过期

控制台输出（`--verbose` 启动）：

```
15:30:00 info OpenShell.Commands: copied fs::C:/a.txt -> fs::C:/b.txt (1024 bytes)
```

### 4. 日志级别语义

| 级别 | 场景 |
|---|---|
| Trace | 极细粒度（每行渲染、补全候选生成） |
| Debug | 命令执行步骤、Provider 调用 |
| Info | 操作完成、命令调用 |
| Warning | 可恢复错误、降级行为、性能异常 |
| Error | 命令失败、Provider 异常 |
| Critical | 进程崩溃、数据丢失风险 |

默认级别 `Info`，`--verbose` 调到 `Debug`，`--trace` 调到 `Trace`。

### 5. Tracing（OpenTelemetry）

每条命令执行是一个 root span：

```csharp
using var activity = _activitySource.StartActivity("Command:" + descriptor.FullName);
activity?.SetTag("command.args", Sanitize(args));
activity?.SetTag("command.user", Environment.UserName);

try
{
    var result = await next();
    activity?.SetStatus(ActivityStatusCode.Ok);
    return result;
}
catch (Exception ex)
{
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    activity?.RecordException(ex);
    throw;
}
```

Span 层级：

```
Command:get-childitem (root)
├── Provider:GetChildrenAsync fs::C:/Users
│   ├── IO:EnumerateDirectory
│   └── Filter:Apply
└── Format:Table
```

### 6. Metrics

```csharp
public interface IMetricsCollector
{
    void IncrementCounter(string name, IReadOnlyDictionary<string, string>? tags = null);
    void RecordHistogram(string name, double value, IReadOnlyDictionary<string, string>? tags = null);
    void RecordGauge(string name, double value, IReadOnlyDictionary<string, string>? tags = null);
}
```

默认指标：

- `openshell_commands_total{command, status}` — 命令调用次数
- `openshell_command_duration_ms{command}` — 命令延迟
- `openshell_provider_calls_total{provider, capability}` — Provider 调用
- `openshell_provider_call_duration_ms{provider, capability}` — Provider 调用延迟
- `openshell_operations_bytes_total{operation, provider}` — 复制/移动字节数
- `openshell_cache_hits_total{cache_type}` / `openshell_cache_misses_total{cache_type}` — 缓存命中
- `openshell_errors_total{category}` — 错误次数

### 7. 诊断包

`export-diagnostics` 命令：

- 收集最近 7 天日志
- 收集系统信息（OS / .NET 版本 / OpenShell 版本）
- 收集配置（不含凭据）
- 收集 metrics 快照
- 打包 zip 到用户指定位置
- 默认排除敏感信息（路径可选包含）

### 8. 脱敏

`ISanitizer` 接口：

```csharp
public interface ISanitizer
{
    string Sanitize(string input);
    IReadOnlyDictionary<string, object?> Sanitize(IReadOnlyDictionary<string, object?> fields);
}
```

规则：
- `access_key` / `secret` / `token` / `password` 字段 → `***`
- 路径字段（`src` / `dst` / `path`）→ 可配置（`log.sanitizePaths = false` 默认不脱敏，敏感场景开启）
- 凭据字段的值禁止进日志
- 用户的 home 路径可配置为脱敏（`log.sanitizeHome = true`）

### 9. 跨进程日志

GUI 与 CLI 子进程：

- 各自维护日志文件（同目录不同文件名）
- 通过 IPC（ADR-0021）的 `traceId` 串联
- 诊断包导出时合并多进程日志

### 10. OpenTelemetry 导出（可选）

`config.toml`：

```toml
[telemetry]
enabled = false                  # 默认关闭
otlpEndpoint = "http://localhost:4317"
serviceName = "openshell"
serviceVersion = "0.1.0"
sampleRate = 0.1                  # 10% 采样
```

启用后 trace / metric 通过 OTLP gRPC 导出到 Jaeger / Tempo / Prometheus。

### 11. 性能影响

- 默认级别 Info，单条日志 < 0.1ms
- Trace 级别开启时影响 < 5%
- Metrics 计数器无锁（`Interlocked`），影响可忽略
- 文件写入异步队列，不阻塞主流程
- 队列满时丢弃 Trace / Debug，保留 Warning+

### 12. 日志查看命令

- `get-log -level error -last 100` — 查阅最近错误
- `tail-log` — 实时跟踪日志输出
- `clear-log` — 清空当前日志文件

### 13. 启动诊断

启动时记录：

- 版本号
- OS 与 .NET 版本
- 加载的 Provider / 命令清单
- 配置文件路径与解析结果

便于用户报问题时附带。

## Alternatives Considered

1. **`Console.WriteLine`**：被否决，无结构化、无级别、无文件
2. **仅 `Microsoft.Extensions.Logging`**：被否决，文件轮转需自己写
3. **NLog / log4net**：被否决，Serilog 的结构化字段更优
4. **不实现 Tracing**：被否决，性能问题难定位
5. **完整 APM 集成（Dynatrace / NewRelic）**：被否决，过重
6. **日志全量进文件不轮转**：被否决，磁盘爆炸

## Consequences

### 优势
- 结构化日志便于分析
- Tracing 定位性能问题
- Metrics 监控运行状态
- 诊断包简化问题报告
- 脱敏保护隐私
- 跨进程关联

### 代价
- 日志栈依赖（Serilog + OTel SDK）
- 脱敏规则维护
- 性能开销（小但存在）
- 用户隐私意识（路径可能敏感）

### 约束
- 日志文件权限 0600
- 凭据字段必须脱敏，不可配置跳过
- 路径脱敏默认关闭，敏感场景用户开启
- Trace 级别日志必须支持运行时关闭（性能考虑）
- 日志队列满时必须丢弃低级别，保留 Error+
- `export-diagnostics` 必须明确告知用户包含哪些数据
- OpenTelemetry 默认关闭，必须用户显式启用
- 采样率必须可配置（避免数据量爆炸）
- 日志写入必须异步，不阻塞主流程
- Span 必须有结束时间（finally 块确保 Stop）
- Metrics 标签基数必须受控（如 command 名有限，禁止用 path 作 label）
