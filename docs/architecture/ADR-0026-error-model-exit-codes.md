# ADR-0026: 错误模型与退出码

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0004 (命令系统), ADR-0007 (操作引擎), ADR-0008 (CLI REPL)

## Context

M0 的 CLI 用 `try/catch + Console.Error.WriteLine` 处理错误，存在：

- 错误信息无结构化字段（用户无法程序化处理）
- 退出码统一为 1，无法区分错误类型
- Provider 错误、操作错误、解析错误混淆
- 无错误恢复建议（如"权限不足，是否提权重试"）
- 错误流与正常输出混在一起，`2>/dev/null` 难以过滤
- 无错误历史（用户想看上次错误）
- GUI 的错误展示与 CLI 不一致

PowerShell 的 `$?` / `$LASTEXITCODE` / `$Error[0]` 模型成熟，但错误视图与异常类耦合重。

## Decision

### 1. ErrorRecord 结构

```csharp
public sealed record ErrorRecord(
    ErrorCategory Category,           // 分类枚举
    string Message,                    // 一行错误信息
    string? Detail = null,             // 多行详情
    ItemPath? TargetPath = null,       // 涉及的路径（若适用）
    string? Operation = null,          // 失败的命令名
    ErrorPhase Phase = ErrorPhase.Unknown,
    Exception? Exception = null,       // 原始异常
    string? Suggestion = null,         // 修复建议
    Guid ErrorId = default);           // 唯一 ID，便于引用
```

### 2. ErrorCategory 枚举

```csharp
public enum ErrorCategory
{
    Unknown = 0,
    ParseError,           // 命令解析失败
    InvalidArgument,      // 参数类型/范围错误
    ProviderError,        // Provider 内部错误
    ProviderNotFound,     // ADR-0001 的 ProviderNotFoundException
    CapabilityNotSupported,// 不支持的能力
    ItemNotFound,         // 路径不存在
    ItemAlreadyExists,    // 创建时已存在
    PermissionDenied,     // 权限不足
    OperationCancelled,   // 用户取消
    OperationTimeout,     // 超时
    OperationFailed,      // 操作引擎失败（cp/mv/rm）
    CircuitBroken,        // 远程熔断
    NetworkError,         // 远程网络错误
    AuthenticationFailed, // 凭据无效
    ConfigurationError,   // 配置错误
    OutOfMemory,
    IOError,
    NotImplemented,
}
```

### 3. ErrorPhase 枚举

```csharp
public enum ErrorPhase
{
    Unknown = 0,
    Parse,           // 命令解析
    ArgumentBinding, // 参数绑定
    ProviderResolution,
    ProviderInitialization,
    Operation,       // 命令执行中
    Cleanup,          // 善后
}
```

### 4. 错误流

```csharp
public interface IErrorStream
{
    void Write(ErrorRecord error);
    ErrorRecord? LastError { get; }
    IReadOnlyList<ErrorRecord> RecentErrors { get; }     // 最近 100 条
    event EventHandler<ErrorRecord>? ErrorWritten;
}
```

CLI 实现：`ConsoleErrorStream`，写入 stderr + ANSI 红色。
GUI 实现：`ObservableErrorStream`，推到 `StatusbarViewModel` 与错误面板。

### 5. 错误渲染

CLI 默认：

```
[error] copy-item: permission denied
  path: fs::C:/Windows/system32/config
  phase: operation
  suggestion: retry with elevated privileges (Run as Administrator)
  error-id: 7f3a2b1c-...
```

`--verbose` 模式追加完整堆栈。
`--json` 模式输出 ErrorRecord JSON。

### 6. 退出码

| 退出码 | 含义 | 场景 |
|---|---|---|
| 0 | 成功 | |
| 1 | 一般错误 | 未分类的错误 |
| 2 | 解析错误 | 命令语法错误 |
| 3 | 参数错误 | 类型转换、范围 |
| 4 | 命令未找到 | |
| 5 | Provider 错误 | |
| 6 | 权限不足 | |
| 7 | 取消 | Ctrl+C / 取消按钮 |
| 8 | 超时 | |
| 9 | 操作失败 | cp/mv/rm 部分失败 |
| 10 | 配置错误 | |
| 64 | 通用失败（兼容 sysexits） | |
| 130 | 中断（兼容 POSIX SIGINT） | |

`$LASTEXITCODE` 变量保存上一条命令退出码，`$?` bool 表示成功/失败。

### 7. 错误与异常的边界

- **Core 内部**：抛具体异常（`ItemNotFoundException` / `PermissionDeniedException` / ...）
- **命令层**：`catch` 异常，转换为 `ErrorRecord`，写入错误流
- **Pipeline**：单元素错误默认跳过 + warning，`--strict` 模式抛出终止管道
- **Host 层**：未捕获的异常 → `ErrorCategory.Unknown` + 完整堆栈

异常类层级：

```csharp
public abstract class OpenShellException : Exception { ... }

public sealed class ItemNotFoundException : OpenShellException { ... }
public sealed class PermissionDeniedException : OpenShellException { ... }
public sealed class ProviderNotFoundException : OpenShellException { ... }
public sealed class CapabilityNotSupported : OpenShellException { ... }
public sealed class OperationCanceledException : OpenShellException { ... }
public sealed class CircuitBrokenException : OpenShellException { ... }
```

### 8. 错误恢复建议

`Suggestion` 字段提供可执行建议：

| 错误 | Suggestion |
|---|---|
| PermissionDenied | "retry with elevated privileges" |
| ItemNotFound | "check path or use get-childitem to enumerate" |
| ProviderNotFound | "register provider via 'register-provider <path>'" |
| CapabilityNotSupported | "this provider does not support X; check 'get-help about_providers'" |
| CircuitBroken | "remote circuit is open; wait 30s or check 'set-remote-config'" |
| AuthenticationFailed | "run 'set-credential <account>' to refresh" |

### 9. 错误历史

`~/.openshell/errors.jsonl` 持久化最近 100 条，便于跨会话查阅：

```jsonl
{"ts":"2026-07-07T15:30:00Z","id":"guid","category":"PermissionDenied","msg":"...","path":"fs::..."}
```

`get-error` 命令查阅，`clear-error` 清除。

### 10. 退出码与 `$?`

```
copy-item fs::a.txt fs::b.txt
$?                       # false
$LASTEXITCODE            # 9
get-error -last          # 查看上一条
```

### 11. GUI 错误展示

- 状态栏显示最近错误图标 + 一行摘要
- 错误面板（F8 切换）显示详细 ErrorRecord
- 操作失败的对话框含 Suggestion 按钮（如"提权重重试"）

### 12. Pipeline 错误策略

```csharp
// Per ADR-0012 revised (2026-07-08): ScriptBlock 形式为主
[Verb("Where", PipelineOnly = true)]
public sealed class WhereObjectCommand : IPipelineTransform
{
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock? FilterScriptBlock,
        bool Strict = false);

    public async IAsyncEnumerable<IItem> Transform(...)
    {
        foreach (var item in input)
        {
            try { if (FilterScriptBlock?.Invoke(item) is true) yield return item; }
            catch (Exception ex) when (!Args.Strict)
            {
                ctx.Host.ErrorStream.Write(new ErrorRecord(
                    Category: ErrorCategory.OperationFailed,
                    Message: $"where skipped item '{item.Path.Display}': {ex.Message}",
                    TargetPath: item.Path));
                continue;
            }
        }
    }
}
```

### 13. 非终止 vs 终止错误

PowerShell 区分 terminating / non-terminating。我们简化为：

- **非终止错误**：写到 ErrorStream，命令继续（如批量 cp 中单文件失败）
- **终止错误**：抛 `OpenShellException`，命令停止，host 捕获

`ErrorAction` 参数（`-ErrorAction Continue/Stop/SilentlyContinue`）控制：

```
copy-item -r fs::a fs::b -ErrorAction Stop
```

- `Continue`（默认）：非终止错误写入流，继续
- `Stop`：非终止错误升级为终止
- `SilentlyContinue`：非终止错误静默跳过

## Alternatives Considered

1. **仅 Console.Error.WriteLine**：被否决，无结构化、无 GUI 复用
2. **直接抛异常到 host**：被否决，批量操作无法继续
3. **每错误类型独立类**：被否决，分类太多
4. **PowerShell ErrorRecord 完整版**：被否决，含 `TargetObject` / `InvocationInfo` 等复杂字段
5. **退出码 = 1 总是**：被否决，无法脚本化区分错误类型

## Consequences

### 优势
- 错误结构化，可程序化处理
- 退出码分类清晰
- CLI / GUI 统一展示
- Suggestion 提升用户体验
- 错误历史可追溯

### 代价
- ErrorRecord 字段较多，写作负担
- 异常 → ErrorRecord 转换需小心
- 错误流与输出流分离需管道支持

### 约束
- 所有命令异常必须转换为 ErrorRecord，禁止裸 Exception 到 host
- `ErrorCategory` 新增需评审（保持稳定枚举）
- `Suggestion` 必须可操作（含具体命令或步骤），禁止"请检查文档"
- 退出码必须遵循本表，禁止自定义（除非扩展本 ADR）
- `errors.jsonl` 文件权限 0600
- 错误历史记录禁止含凭据（即使是 URL 也要脱敏）
- `ErrorAction` 仅对非终止错误生效，终止错误必须传播
- Pipeline 错误默认非终止（跳过 + warning），`--strict` 升级为终止
- 异常类必须继承 `OpenShellException`，便于统一捕获
- `ErrorRecord` 是 `sealed record`，不可变
