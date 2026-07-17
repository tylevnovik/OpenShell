# ADR-0059: 远程基础设施 — SSH PSSession 与脚本块序列化

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M6 (Remoting)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0047 (Variable System), ADR-0054 (ExecutionPolicy), ADR-0058 (JIT Compilation)
- **Implementation Status**: M6 已实现 (2026-07-08): §1-§8 完整远程框架, 包含 IPSSession / SshPSSession (基于 ssh 子进程 + JSON-Lines 协议), PSSessionManager (会话生命周期管理), ScriptBlockSerializer (ScriptBlock ↔ JSON 双向序列化, $using: 变量捕获), Evaluator $using: 求值集成 (Invoke-Command 上下文下从 UsingValues 注入), New-PSSession / Invoke-Command / Enter-PSSession / Exit-PSSession / Get-PSSession / Remove-PSSession 命令, AddRemoting() DI 扩展。WinRM 传输层文档化但未实现 (§9)。

## Context

OpenShell 当前 (M5 性能层完成、M6 远程层启动) 为**单机 shell**: 所有命令在本地进程执行, 无法跨主机编排。运维场景普遍需要远程执行:

1. **批量管理**: 在数十台主机上执行同一脚本块 (配置下发 / 健康检查 / 日志采集)
2. **跨平台编排**: 从 Windows 管理 Linux 主机, 或反向; OpenShell 跨平台特性需延伸到远程
3. **PowerShell 兼容**: 用户期望 `Invoke-Command -Session $s { Get-Process }` / `Enter-PSSession` 体验
4. **$using: 语义**: 远程脚本块需捕获本地变量 (`$using:filter`), 与 PowerShell 5.1+ 语义对齐
5. **安全传输**: 不依赖 WinRM (Windows 限定 / 配置复杂), 优先 SSH (跨平台 / 普及度高 / 密钥认证成熟)

### 设计原则

1. **SSH 优先**: 默认传输为 SSH, 利用系统 `ssh` 客户端 + 密钥认证; WinRM 文档化但暂不实现 (§9)
2. **JSON-Lines 协议**: 远程进程 stdin/stdout 走 JSON 行 (每行一个对象), 简单可调试, 与 ADR-0044 流式语义对齐
3. **脚本块一等序列化**: `ScriptBlock` 可序列化为 JSON (源文本 + 参数 + $using 捕获), 远端反序列化后用本地 Evaluator 执行
4. **$using: 显式捕获**: `Invoke-Command` 求值时扫描脚本块 AST 中的 `$using:name` 引用, 在本地求值后随序列化载荷传递, 远端注入为 Local 变量
5. **会话复用**: `New-PSSession` 建立持久 ssh 连接, 多次 `Invoke-Command -Session` 复用同一连接 (避免握手开销)
6. **安全回退**: ssh 不可用 / 远端非 OpenShell 时返回明确错误, 不静默失败
7. **ExecutionPolicy 联动**: 远端执行受远端 ExecutionPolicy 约束 (per ADR-0054 §9), 序列化脚本块视为 "远程来源"

### 依赖关系

- **上游依赖**:
  - ADR-0046 (Script Blocks): ScriptBlock 是远程执行的基本载荷, 序列化需保留 AST 语义
  - ADR-0047 (Variable System): `$using:` 变量从 Local 作用域捕获, 远端注入为 Local
  - ADR-0054 (ExecutionPolicy): 远端加载序列化脚本块触发 ExecutionPolicy 检查
  - ADR-0058 (JIT Compilation): 远端 Evaluator 自动享受 JIT 缓存 (透明)
- **下游依赖**:
  - 未来 ADR: WinRM 传输 / 远端调试器 / 远端 Job 调度

## Decision

### 1. IPSSession 抽象

```csharp
public interface IPSSession : IAsyncDisposable
{
    int Id { get; }
    string ComputerName { get; }
    string Transport { get; }      // "SSH" (未来可扩展 "WinRM")
    PSSessionState State { get; }
    Task<object?> InvokeAsync(SerializedScriptBlock payload, CancellationToken ct);
}

public enum PSSessionState { Disconnected, Opened, Closed, Faulted }
```

**设计要点**:
- `Id` 由 `PSSessionManager` 分配的全局唯一整数 (递增)
- `InvokeAsync` 接收已序列化的脚本块载荷, 返回远端最终结果值
- `IAsyncDisposable` 确保会话关闭时释放 ssh 子进程

### 2. SshPSSession 实现

`SshPSSession` 通过启动 `ssh user@host` 子进程建立持久会话, 远端运行 `openshell --no-interactive --receive-serialized` 进入 JSON-Lines 接收模式:

```
本地进程                     ssh 子进程                   远端 openshell
    │                            │                            │
    │── write JSON line ────────▶│── stdin ──────────────────▶│
    │                            │                            │── Evaluator.Execute
    │                            │◀── stdout ─────────────────│── write JSON line
    │◀── read JSON line ─────────│                            │
```

**协议消息** (每行一个 JSON 对象):

| 类型 | 方向 | 字段 | 说明 |
|---|---|---|---|
| `invoke` | 本地→远端 | `script`, `using`, `args` | 请求执行脚本块 |
| `result` | 远端→本地 | `value`, `stream[]` | 返回最终值 + 流式输出 |
| `error` | 远端→本地 | `message`, `category` | 远端执行错误 |
| `stream` | 远端→本地 | `kind`, `value` | 流式输出项 (输出/警告/错误) |

**关键字段**:
- `script`: 脚本块源文本 (远端重新解析)
- `using`: `{ "name": value, ... }` 字典, 本地捕获的 `$using:` 变量
- `args`: 位置参数数组

**远端命令**: `openshell --no-interactive --receive-serialized` (本 ADR 定义该 CLI 入口; 若远端未安装 OpenShell 则 ssh 失败, 返回明确错误)

### 3. PSSessionManager

```csharp
public sealed class PSSessionManager
{
    public IPSSession Create(PSSessionOptions options);
    public IPSSession? Get(int id);
    public IReadOnlyList<IPSSession> GetAll();
    public void Remove(int id);
}
```

**职责**:
- 分配递增 `Id`
- 维护 `ConcurrentDictionary<int, IPSSession>` 会话表
- `Remove` 触发 `DisposeAsync` (关闭 ssh 子进程)
- 单例 (DI 注册), 跨命令共享会话

### 4. ScriptBlockSerializer

```csharp
public static class ScriptBlockSerializer
{
    public SerializedScriptBlock Serialize(ScriptBlock block, ExecutionContext ctx, object?[] args);
    public ScriptBlock Deserialize(SerializedScriptBlock payload, ExecutionContext remoteCtx);
}

public sealed record SerializedScriptBlock(
    string Script,                       // 脚本块源文本
    IReadOnlyDictionary<string, object?> UsingValues,  // $using: 捕获
    IReadOnlyList<object?> Args);
```

**序列化流程** (`Serialize`):
1. 取 `block.Ast.SourceText` (若为 null 则用 `block.ToString()`)
2. **扫描 `$using:` 引用**: 遍历 AST, 收集所有 `VariableExpression { Scope = Using }` 的变量名
3. 对每个 `$using:name` 在**本地** `ctx.Variables` 求值, 填入 `UsingValues`
4. 序列化值用 `System.Text.Json` (基础类型 + 嵌套对象; 不支持闭包 / 类型实例, 报错)

**反序列化流程** (`Deserialize`):
1. 用 `ModernParser.Parse(Script)` 解析源文本为 `ScriptBlockAst`
2. 构造 `ScriptBlockExpression` + `ScriptBlock`
3. `UsingValues` 由调用方注入到远端 `ExecutionContext.Variables` 的 Local 作用域 (见 §5)

### 5. $using: 变量解析

**本地侧** (`Invoke-Command` 命令内):
- `Invoke-Command -Session $s -ScriptBlock { ... $using:filter ... }`
- 命令调用 `ScriptBlockSerializer.Serialize`, 序列化器扫描 AST 中 `VariableScopeKind.Using` 节点, 在本地求值后放入 `UsingValues`

**远端侧** (远端 Evaluator):
- 远端收到 `invoke` 消息后, 创建临时 Local 作用域, 把 `UsingValues` 逐项 `Set` 进变量表
- 远端 Evaluator 求值 `$using:name` 时走 `VariableScopeKind.Using => Resolve(name, Local)` (per ADR-0047 §1.2, 已实现于 Evaluator.cs:853), 命中注入的 Local 值
- 执行完毕后弹出 Local 作用域, `$using:` 变量不泄漏

**Evaluator 无需修改**: 现有 `EvaluateVariable` 已把 `$using:` 映射到 `VariableScope.Local` 查找 (ADR-0047 §1.2), 远端只需把 `UsingValues` 注入 Local 作用域即可复用语义。

### 6. 命令

| 命令 | 动词 | 别名 | 说明 |
|---|---|---|---|
| `New-PSSession` | `New` | `nsn` | 建立 SSH 会话, 返回 IPSSession |
| `Invoke-Command` | `Invoke` | `icm` | 在会话上执行脚本块 |
| `Enter-PSSession` | `Enter` | `etsn` | 进入交互式会话 (REPL 重定向) |
| `Exit-PSSession` | `Exit` | `exsn` | 退出交互式会话 |
| `Get-PSSession` | `Get` | `gsn` | 列出所有会话 |
| `Remove-PSSession` | `Remove` | `rsn` | 关闭并移除会话 |

**New-PSSession 参数**:
- `-HostName` (string, 必填): 目标主机 (`user@host` 或 `host`)
- `-Name` (string, 可选): 会话友好名

**Invoke-Command 参数**:
- `-Session` (IPSSession, 必填): 目标会话
- `-ScriptBlock` (ScriptBlock, 必填): 要执行的脚本块
- `-ArgumentList` (object[], 可选): 位置参数

**Invoke-Command 流程**:
1. `ScriptBlockSerializer.Serialize(ScriptBlock, ctx, ArgumentList)`
2. `session.InvokeAsync(payload, ct)` (发送 JSON, 等待 result)
3. 把远端 `result.value` 转为 IItem 流 yield

### 7. DI 扩展

```csharp
public static class RemotingServiceCollectionExtensions
{
    public static IServiceCollection AddRemoting(this IServiceCollection services)
    {
        services.AddSingleton<PSSessionManager>();
        return services;
    }
}
```

`PSSessionManager` 为单例, 跨命令共享会话表。`SshPSSession` / `ScriptBlockSerializer` 为瞬态 (按需构造, 不需 DI)。

### 8. 安全考量

1. **SSH 认证**: 依赖系统 ssh 客户端的密钥 / agent 认证, OpenShell **不存储密码**
2. **远端 ExecutionPolicy**: 序列化脚本块在远端视为 "远程来源" (isRemote=true), 受远端 ExecutionPolicy 约束 (per ADR-0054 §9)
3. **$using: 值过滤**: 序列化器仅传递可 JSON 化的基础类型 (string/number/bool/null/array/object); 闭包 / 类型实例 / 文件句柄拒绝序列化
4. **命令注入防护**: ssh 参数严格转义; 远端脚本通过 stdin 传递 (不拼接命令行)
5. **审计**: `New-PSSession` / `Invoke-Command` 应触发审计事件 (per ADR-0036 §审计; 当前实现写入错误流日志, 完整审计待未来 ADR)

### 9. WinRM (未实现, 文档化)

PowerShell 原生远程使用 WinRM (WS-Management 协议)。OpenShell 跨平台优先 SSH, WinRM 仅文档化:

**未来实现要点** (不在本 ADR 范围):
- `WinRMPSSession : IPSSession` 实现 WS-Management SOAP 客户端
- 依赖 `Microsoft.WSMan.Management` 或自研 SOAP 客户端
- 仅 Windows 远端可用
- 配置复杂 (TrustedHosts / Enable-PSRemoting / 防火墙), 跨平台场景受限

**当前状态**: `Transport` 字段预留 `"WinRM"` 值, 但 `PSSessionManager.Create` 仅支持 SSH 选项。`WinRMPSSession` 类未实现。

## Consequences

- **正面**:
  - 跨平台远程执行 (Windows ↔ Linux ↔ macOS) 开箱即用 (依赖 ssh)
  - `$using:` 语义与 PowerShell 对齐, 迁移成本低
  - 会话复用减少握手开销
  - JSON-Lines 协议可调试 (可用 `openshell --receive-serialized` 手动测试)
- **负面**:
  - 远端必须安装 OpenShell (不像 PowerShell WinRM 可用任意 PS 实例)
  - ssh 子进程生命周期管理增加复杂度 (进程崩溃 / 网络中断)
  - 序列化仅支持基础类型, 复杂对象 (类型实例 / 闭包) 无法跨主机传递
  - 交互式 `Enter-PSSession` 体验受限 (REPL 重定向, 非真终端)
- **中性**:
  - WinRM 用户需等待未来 ADR; 当前可降级为 SSH

## Implementation Notes

- `SshPSSession` 使用 `System.Diagnostics.Process` 启动 ssh, stdin/stdout 重定向为 UTF-8
- JSON 序列化用 `System.Text.Json` (`JsonSerializerOptions { PropertyNamingPolicy = CamelCase }`)
- `$using:` AST 扫描用 `AstWalker` 模式 (递归遍历 Expression 节点, 收集 `VariableExpression { Scope = Using }`)
- `Enter-PSSession` 当前实现为: 把后续 REPL 输入通过 `session.InvokeAsync` 转发; `Exit-PSSession` 恢复本地 REPL (简化实现, 非完整终端代理)
- 远端 `--receive-serialized` CLI 入口由宿主 (Program.cs) 注册, 本 ADR 仅定义协议; orchestrator 负责 CLI 接线
