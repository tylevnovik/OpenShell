# ADR-0036: 安全沙箱与权限模型

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: 长期可选
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0007 (操作引擎), ADR-0026 (错误), ADR-0022 (配置)

## Context

OpenShell 是强大的 shell + 文件管理器，伴随风险：

1. **破坏性操作**：`remove-item -r fs::C:/` 可清空磁盘
2. **远程执行**：`invoke-expression` 风格命令可能执行任意代码
3. **凭据泄露**：日志 / 历史可能含凭据
4. **第三方 Provider 风险**：插件代码执行任意逻辑
5. **跨 Provider 危险操作**：`copy-item -r fs::C:/ s3://public-bucket/` 数据外泄
6. **提权**：注册表 / 系统目录写入需管理员
7. **审计需求**：企业部署需记录敏感操作

需求约束：

- 不阻塞日常使用（默认安全，不频繁打扰）
- 用户可配置严格度
- 操作可审计
- 第三方 Provider 可限制能力范围
- 提权流程清晰

## Decision

### 1. 操作风险等级

```csharp
public enum OperationRisk
{
    Safe,           // get-*, list-*，只读
    Low,            // copy/move 普通文件
    Medium,        // new-item, set-property
    High,          // remove-item, set-content 系统文件
    Critical,      // remove-item -r 根目录, 远程上传大文件
    Destructive,   // remove-item -force 物理删除, 注册表 HKLM 写入
}
```

### 2. 默认确认策略

按风险等级确认：

| 风险 | 默认行为 |
|---|---|
| Safe | 直接执行 |
| Low | 直接执行 |
| Medium | 直接执行（用户可开启确认） |
| High | CLI 二次确认（`Proceed? [y/N]`） |
| Critical | CLI 强制确认 + GUI 对话框 |
| Destructive | 必须显式 `--force` 或 `--i-know-what-im-doing` |

### 3. 危险模式自动检测

```csharp
public sealed class RiskAnalyzer
{
    public OperationRisk Analyze(string command, object args, CommandContext ctx)
    {
        return command switch
        {
            "remove-item" => AnalyzeRemove(args, ctx),
            "copy-item" => AnalyzeCopy(args, ctx),
            "set-content" => AnalyzeSetContent(args, ctx),
            _ => OperationRisk.Low
        };
    }

    private OperationRisk AnalyzeRemove(object args, CommandContext ctx)
    {
        var path = GetPath(args);
        if (path is null) return OperationRisk.Low;

        // 根目录递归删除
        if (IsRoot(path) || IsSystemDirectory(path))
            return OperationRisk.Critical;

        // 大批量删除
        if (GetItemCount(path) > 1000)
            return OperationRisk.Critical;

        // 物理删除（不走 Trash）
        if (GetForce(args))
            return OperationRisk.Destructive;

        return OperationRisk.High;
    }
}
```

### 4. 受保护路径

```toml
# config.toml
[security]
protectedPaths = [
    "fs::C:/Windows",
    "fs::C:/Program Files",
    "fs::C:/Program Files (x86)",
    "fs::/etc",
    "fs::/usr",
    "fs::/bin",
    "reg::HKLM/SAM",
    "reg::HKLM/SECURITY",
]
```

受保护路径的 `remove-item` 必须显式 `--force`，且记录审计日志。

### 5. 操作审计

`~/.opensshell/audit.jsonl`：

```jsonl
{"ts":"2026-07-07T15:30:00Z","user":"me","command":"remove-item","args":"fs::C:/sensitive","risk":"Critical","approved":true,"approvedBy":"user-prompt"}
```

记录：

- 时间戳
- 用户
- 命令与参数
- 风险等级
- 是否批准 + 来源（prompt / config / auto）

文件权限 0600，保留 30 天。

### 6. Provider 沙箱

第三方 Provider 加载时声明能力范围：

```csharp
[assembly: ProviderAssembly(
    "my-provider",
    "1.0.0",
    Sandbox = ProviderSandbox.None)]   // None / ReadOnly / Restricted / Full

public sealed class ProviderSandbox
{
    public IReadOnlySet<string> AllowedReadPaths { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> AllowedWritePaths { get; init; } = new HashSet<string>();
    public bool NetworkAccess { get; init; }
    public bool ProcessSpawn { get; init; }
}
```

加载时校验声明，运行时拦截越界访问：

- 声明 `ReadOnly` 但调写接口 → 拒绝
- 声明 `NetworkAccess = false` 但尝试 HTTP → 拦截

### 7. 提权流程

#### Windows

- UAC 提示（`runas` verb 启动新进程）
- 当前进程不提权，子进程提权执行后退出

#### Linux / macOS

- `sudo` 命令调用
- GUI 弹密码框（pkexec Linux / osascript macOS）

`elevate` 命令：

```
elevate remove-item fs::C:/Windows/old
```

执行流程：

1. 检测需要提权
2. 弹 UAC / sudo 密码框
3. 启动子进程执行
4. 等待退出，回收结果

### 8. 命令权限矩阵

```csharp
[Verb("Remove", Noun = "Item")]
[RequiredPermission(PermissionLevel.High)]
public sealed class RemoveItemCommand : ...
```

权限级别：

- `ReadOnly` — 默认，无需确认
- `Low` — 写普通文件
- `High` — 写系统文件、注册表
- `Critical` — 物理删除、远程批量

启动时根据 `config.toml` 的用户角色映射命令级别。

### 9. 角色

```toml
[security]
role = "user"     # user / admin / restricted
```

- `user`：默认，遵守风险等级
- `admin`：跳过 High 及以下确认
- `restricted`：禁止 High 及以上操作

### 10. 凭据保护

- 凭据存储用 OS 加密（ADR-0019）
- 日志 / 历史 / 审计中凭据字段脱敏（ADR-0031）
- 命令参数含凭据时历史不记录（如 `set-credential --secret xxx`）

### 11. 网络访问控制

Provider 声明 `NetworkAccess`：

- `Remote Provider`：允许
- `Archive Provider`：不允许
- `Registry Provider`：不允许（本地）

加载时检测，运行时通过 `HttpClient` 拦截器验证来源。

### 12. 进程生成

`invoke-expression` / `start-process` 命令需声明 `ProcessSpawn` 权限：

- 默认 CLI host 允许
- GUI host 默认禁止，需配置
- 第三方 Provider 禁止

### 13. 文件权限保护

- 受保护路径写入需 `--force`
- 隐藏文件删除需 `--force`
- 系统文件修改需提权

### 14. 用户可配置严格度

```toml
[security]
strictness = "default"   # lax / default / strict / paranoid
```

| 严格度 | High | Critical | Destructive |
|---|---|---|---|
| lax | 直接执行 | 确认 | 确认 |
| default | 直接执行 | 确认 | 必须 --force |
| strict | 确认 | 二次确认 | 必须 --force + 审计 |
| paranoid | 二次确认 | 二次确认 + 密码 | 必须 --force + 审计 + 密码 |

### 15. 审计查看

- `get-audit` — 查阅审计日志
- `clear-audit` — 清除（需 `--force`）

## Alternatives Considered

1. **无沙箱，信任用户**：被否决，误操作风险大
2. **完整 .NET Code Access Security**：被否决，CAS 已弃用
3. **进程隔离每 Provider**：被否决，性能开销
4. **AppContainer / Sandbox 沙箱**：被否决，跨平台难
5. **完全禁用危险命令**：被否决，灵活度不足

## Consequences

### 优势
- 误操作保护
- 危险操作审计
- 第三方 Provider 沙箱
- 提权流程清晰
- 用户可配置严格度

### 代价
- 实现复杂
- 性能开销（沙箱拦截）
- 严格模式可能打扰用户
- 审计日志存储

### 约束
- 审计文件权限 0600
- 审计记录禁止含凭据（即使参数有也脱敏）
- 受保护路径列表必须可用户扩展
- `--force` 必须明确警示用户
- 提权失败必须明确报错，不静默跳过
- 第三方 Provider 沙箱声明必须经加载校验，缺失则拒绝
- `paranoid` 模式密码提示必须用 OS 原生输入（避免 keylogger）
- 网络拦截必须记录来源 Provider
- `Restricted` 角色下禁止 High+ 操作，必须可被用户改为 `user` 临时提权
- 审计日志保留期可配置，默认 30 天
- 凭据相关命令必须从历史中排除
