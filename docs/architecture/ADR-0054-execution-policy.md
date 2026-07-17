# ADR-0054: ExecutionPolicy — 脚本执行策略

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M5+ (Infrastructure)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0036 (Security Sandbox), ADR-0039 (Provider Package Ecosystem), ADR-0046 (Script Blocks), ADR-0049 (ShouldProcess), ADR-0042 (Automatic Variables)
- **Implementation Status**: M5+ 已实现 (2026-07-08): §1-§10 完整执行策略框架, 包含 ExecutionPolicy 枚举, ExecutionPolicyService (Machine/User/Process 三级优先级), Zone.Identifier (Windows ADS) / xattr (Unix) 远程文件检测, Ed25519 签名校验集成, Get-ExecutionPolicy / Set-ExecutionPolicy 命令, -ExecutionPolicy CLI flag, OpenShellConfig.ExecutionPolicy 配置字段, Evaluator 集成 (脚本加载前检查), AddExecutionPolicy() DI 扩展。

## Context

ADR-0046 §621 / §706 与 ADR-0049 §44 明确将 ExecutionPolicy 推迟到独立 ADR 决策。当前 OpenShell 缺乏对脚本执行的安全控制——任意 `.osh` / `.ps1` 文件均可直接加载执行, 无远程来源检测、无签名校验、无策略分级。这与 PowerShell 的 `ExecutionPolicy` 机制存在显著差距, 在企业部署、CI/CD、用户从网络下载脚本的场景下构成安全风险。

### 问题域

1. **远程脚本风险**: 用户从浏览器 / 邮件附件 / `Invoke-WebRequest` 下载的 `.osh` / `.ps1` 文件可能含恶意代码 (删除文件、外泄数据、提权等)。当前无任何拦截机制。
2. **签名基础设施闲置**: ADR-0039 §8 已建立 `ISignatureVerifier` (Ed25519) 用于 `.osp` 包校验, 但仅适用于 Provider 包, 未扩展到 `.osh` 脚本签名。
3. **企业策略缺失**: 企业部署需统一控制脚本执行权限 (如 CI 强制 `Bypass`、生产机强制 `AllSigned`), 当前无 Machine 级配置。
4. **与 ADR-0036 沙箱的关系**: ADR-0036 处理**操作级**风险 (remove-item / set-content 等), 而 ExecutionPolicy 处理**脚本加载级**风险——后者是前者的前置门, 必须先通过 ExecutionPolicy 才能进入 RiskAnalyzer 流程。
5. **PowerShell 兼容**: PowerShell 5.1+ 的 `Get-ExecutionPolicy` / `Set-ExecutionPolicy` / `-ExecutionPolicy` CLI flag / `# ExecutionPolicy: RemoteSigned` pragma 是事实标准, OpenShell 需对齐以支持已有 PS 脚本迁移。

### 设计原则

1. **PowerShell 全兼容**: 4 个策略级别 (`Restricted` / `RemoteSigned` / `Unrestricted` / `Bypass`)、3 级 scope (Machine / User / Process)、`Get-ExecutionPolicy` / `Set-ExecutionPolicy` 命令签名与 PowerShell 对齐。
2. **复用现有基础设施**: 签名校验复用 ADR-0039 `ISignatureVerifier`, 配置持久化复用 ADR-0022 `OpenShellConfig`, 安全服务集成复用 ADR-0036 `ISecurityService` 协调。
3. **不阻塞日常使用**: 默认策略 `RemoteSigned` 允许本地脚本自由执行, 仅拦截远程未签名脚本; 用户可通过 `-ExecutionPolicy Bypass` 临时绕过 (CI/CD 场景)。
4. **跨平台**: Windows 用 NTFS Alternate Data Stream (`Zone.Identifier`) 检测远程文件; Unix 用 `user.xdg.origin.url` / `user.openshell.remote` xattr; macOS 同 Unix。
5. **失败保守**: 签名校验失败、文件读取失败、xattr 读取失败时, 默认拒绝执行 (保守策略, 避免静默放行恶意脚本)。

## Decision

### 1. ExecutionPolicy 枚举

```csharp
public enum ExecutionPolicy
{
    Restricted,     // 禁止执行任何脚本 (.osh / .ps1), 仅允许交互式 REPL
    RemoteSigned,   // 本地脚本自由执行; 远程脚本需 Ed25519 签名
    Unrestricted,   // 所有脚本可执行, 远程脚本弹确认提示
    Bypass,         // 无任何限制 (CI/CD 场景)
}
```

- 不实现 `AllSigned` (PowerShell 7 已逐步弱化此级别, 且会强制所有脚本签名, 与 OpenShell 默认 `RemoteSigned` 体验冲突)。
- 不实现 `Default` 别名 (PowerShell 中 `Default` 等价 `Restricted` on Windows Client / `RemoteSigned` on Server, 跨平台语义不清晰)。
- 默认策略: `RemoteSigned` (与 PowerShell on Linux/macOS 一致)。

### 2. 策略 Scope 优先级

```
Process > User > Machine
```

| Scope | 来源 | 持久化 | 优先级 |
|---|---|---|---|
| Process | `-ExecutionPolicy` CLI flag / `$env:OPENSHELL_EXECUTION_POLICY` | 进程内, 退出即失效 | 最高 |
| User | `~/.openshell/config.toml` 的 `executionPolicy` 字段 | 用户级, 跨进程 | 中 |
| Machine | Windows: `HKLM\SOFTWARE\OpenShell\ExecutionPolicy`; Unix: `/etc/openshell/policy.toml` | 系统级, 需管理员 | 低 |

`GetEffectivePolicy()` 返回最高优先级的非 `Default` 值; 全部为 `Default` 时回落到 `RemoteSigned`。

**约束**:
- Process scope 不能被 User / Machine 覆盖 (CI/CD 显式指定时尊重之)。
- User scope 不能高于 Machine scope 的限制 (即 User 设 `Bypass` 但 Machine 设 `Restricted` 时, 有效策略为 `Restricted`)。这是 PowerShell 的语义对齐, 防止用户绕过企业策略。
- 脚本内 `# ExecutionPolicy:` pragma 不能提升策略级别 (只能收紧, 不能放宽)。

### 3. 远程文件检测

#### Windows (NTFS ADS)

读取 `file:Zone.Identifier` ADS, 解析 `[ZoneTransfer] ZoneId=N`:
- `ZoneId=0` (MyComputer): 本地
- `ZoneId=1` (Local Intranet): 本地
- `ZoneId=2` (Trusted Sites): 本地 (受信任)
- `ZoneId=3` (Internet): **远程**
- `ZoneId=4` (Restricted Sites): **远程**

实现: `FileStream` with `FileStreamOptions(Options := FileOptions.None)` + alternate stream 路径 `file:Zone.Identifier`。

#### Unix / macOS (xattr)

- Linux: 读取 `user.xdg.origin.url` (桌面环境标准) 或 `user.openshell.remote` (OpenShell 自定义)
- macOS: 读取 `com.apple.metadata:kMDItemWhereFroms` 或 `com.apple.quarantine` ( quarantine 属性存在即视为远程)

实现: 调用 `xattr` / `attr` 命令行工具 (依赖-free, 避免引入 native interop)。失败时返回 `false` (保守不视为远程)。

### 4. 签名校验集成

复用 ADR-0039 §8 的 `ISignatureVerifier` (Ed25519 detached signature):

- `.osh` 脚本可选附旁路签名文件 `<script>.osh.sig` + `<script>.osh.pub`
- `ExecutionPolicyService.CheckSignature(filePath)`:
  1. 读取 `<script>.sig` 与 `<script>.pub` (不存在 → `Untrusted`)
  2. 计算 `<script>` 内容的 SHA256 哈希作为 `payloadHash`
  3. 调用 `ISignatureVerifier.VerifyAsync(manifest, payloadHash, publicKey, signature, sourceIsTrusted: false, ct)`
  4. 返回 `SignatureResult.Valid` / `Invalid` / `Untrusted` / `TrustedSource`

**ProviderManifest 适配**: `ISignatureVerifier.VerifyAsync` 第一个参数为 `ProviderManifest`, 对脚本场景传入 null (实现需容忍 null manifest, 仅用 payloadHash)。本 ADR 修改 `Ed25519SignatureVerifier` 不依赖 manifest (实际已如此, manifest 仅用于日志)。

### 5. `CanExecute(filePath, isRemote)` 决策矩阵

| Policy | 本地脚本 | 远程签名脚本 | 远程未签名脚本 |
|---|---|---|---|
| `Restricted` | ❌ 拒绝 (仅 REPL) | ❌ 拒绝 | ❌ 拒绝 |
| `RemoteSigned` | ✅ 执行 | ✅ 执行 (签名有效) | ❌ 拒绝 (需签名) |
| `Unrestricted` | ✅ 执行 | ✅ 执行 | ⚠️ 弹确认提示 |
| `Bypass` | ✅ 执行 | ✅ 执行 | ✅ 执行 (无任何检查) |

返回 `(bool canExecute, string reason)`, `reason` 用于错误信息与审计日志。

### 6. `Get-ExecutionPolicy` / `Set-ExecutionPolicy` 命令

```powershell
Get-ExecutionPolicy                # 返回当前有效策略 (Process > User > Machine)
Get-ExecutionPolicy -List          # 列出所有 scope 的策略
Set-ExecutionPolicy RemoteSigned           # 设置 User scope
Set-ExecutionPolicy Bypass -Scope Process  # 设置 Process scope (仅当前会话)
Set-ExecutionPolicy Restricted -Scope Machine  # 设置 Machine scope (需管理员)
```

### 7. `-ExecutionPolicy` CLI flag

```
openshell --execution-policy Bypass script.osh
openshell --execution-policy RemoteSigned
```

- 仅 Process scope, 退出即失效。
- 解析后写入 `$env:OPENSHELL_EXECUTION_POLICY` (供子进程继承)。
- Program.cs 解析该 flag 但**不在此处 wire DI** (orchestrator 处理 DI 注册); `ExecutionPolicyService` 通过 DI 解析时读取环境变量。

### 8. `# ExecutionPolicy: RemoteSigned` pragma

脚本头部首行注释可声明文件级策略提示:

```kotlin
# ExecutionPolicy: RemoteSigned
import "helper.osh"
```

- 仅作为**提示**, 不能提升系统策略 (即系统策略为 `Restricted` 时, 文件 pragma 为 `RemoteSigned` 仍拒绝执行)。
- 用于: CI/CD 场景下脚本作者声明"此脚本需要至少 RemoteSigned 才能正常运行", 避免用户在 `Restricted` 下困惑为何脚本不执行。
- 实现: `ExecutionPolicyService` 解析文件首 10 行, 匹配 `#\s*ExecutionPolicy:\s*(\w+)`。

### 9. 与 ADR-0036 SecurityService 集成

ExecutionPolicy 检查发生在 **RiskAnalyzer 之前** (脚本加载层 vs 操作执行层):

```
用户执行 script.osh
  → ExecutionPolicyService.CanExecute(path)  ← 本 ADR
    → 通过 → Evaluator.Execute(ast)
      → 命令调用 → ISecurityService.AssessRisk  ← ADR-0036
    → 拒绝 → 写错误流, 返回
```

`ExecutionPolicyService` 不依赖 `ISecurityService` (单向调用), 但审计日志走 `IAuditService` (复用 ADR-0036 §5)。

### 10. 配置字段

`OpenShellConfig` 新增:

```csharp
/// <summary>脚本执行策略: "Restricted" / "RemoteSigned" / "Unrestricted" / "Bypass"。Per ADR-0054. 默认 "RemoteSigned"。</summary>
public string ExecutionPolicy { get; set; } = "RemoteSigned";
```

## Costs

### 优势

- **安全门控**: 远程未签名脚本默认拒绝, 防止用户误执行下载的恶意脚本。
- **PowerShell 兼容**: `Get-ExecutionPolicy` / `Set-ExecutionPolicy` / `-ExecutionPolicy` 与 PowerShell 对齐, 已有 PS 脚本与运维流程零成本迁移。
- **复用基础设施**: Ed25519 签名校验复用 ADR-0039 实现, 配置持久化复用 ADR-0022, 审计日志复用 ADR-0036, 无新依赖引入。
- **企业可控**: Machine scope 允许企业统一策略, 用户无法绕过。
- **跨平台**: Windows ADS + Unix xattr 双路径, 覆盖主流 OS。

### 代价

- **签名基础设施成本**: 用户需自行生成 Ed25519 密钥对并分发公钥, 对个人用户门槛较高。缓解: 默认 `RemoteSigned` 不强制签名, 仅远程脚本需要。
- **远程检测局限**: xattr 在某些文件系统 (FAT32 / exFAT / 网络挂载) 不可用, 远程文件可能漏检。缓解: 签名校验作为第二道防线。
- **性能开销**: 每次脚本加载需读 ADS / xattr + 计算 SHA256 (仅远程文件)。典型场景 < 1ms, 可接受。
- **Machine scope 实现复杂度**: Windows 注册表 + Unix `/etc/openshell/policy.toml` 双路径, 需管理员权限处理逻辑。
- **pragma 仅提示**: 用户可能误以为 pragma 能提升策略, 需文档强调。

### 约束

- `ExecutionPolicy` 枚举仅 4 个值: `Restricted` / `RemoteSigned` / `Unrestricted` / `Bypass`。不实现 `AllSigned` / `Default`。
- 默认策略: `RemoteSigned` (跨平台一致, 与 PowerShell on Linux/macOS 对齐)。
- Process scope 优先级最高, 不能被 User / Machine 覆盖。
- User scope 不能高于 Machine scope (即 Machine 设 `Restricted` 时 User 设 `Bypass` 仍为 `Restricted`)。
- 脚本内 `# ExecutionPolicy:` pragma 不能提升策略级别, 只能收紧。
- 远程检测: Windows 用 `Zone.Identifier` ADS, Unix 用 xattr; 失败时保守不视为远程 (但签名仍需校验)。
- 签名校验: `.osh` 脚本旁路文件 `<script>.sig` + `<script>.pub`, Ed25519 算法, 复用 `ISignatureVerifier`。
- 签名校验失败 (`Invalid`) → 拒绝执行; 签名不存在 (`Untrusted`) → 按策略决定 (`RemoteSigned` 拒绝, `Unrestricted` 弹确认)。
- `Set-ExecutionPolicy -Scope Machine` 需管理员权限 (Windows: UAC; Unix: root), 失败时返回 `UnauthorizedAccessException`。
- ExecutionPolicy 检查在 RiskAnalyzer 之前, 是脚本加载层的前置门。
- `-ExecutionPolicy` CLI flag 仅 Process scope, 退出即失效。
- `# ExecutionPolicy:` pragma 仅在文件首 10 行解析, 格式 `#\s*ExecutionPolicy:\s*(\w+)`。
- Machine scope 不可用时 (注册表访问失败 / `/etc/openshell/policy.toml` 不存在), 降级到 User scope, 不阻断启动。

## Alternatives Considered

1. **仅 `Restricted` / `Bypass` 两级**: 被否决。理由: 缺乏 `RemoteSigned` 的细粒度控制, 要么完全禁止脚本 (日常使用不便), 要么完全放行 (安全风险), 无法平衡。

2. **引入 `AllSigned` 级别**: 被否决。理由: 强制所有脚本签名对个人用户门槛过高, 与 OpenShell "默认易用" 目标冲突。`RemoteSigned` 已覆盖主要风险场景 (远程脚本)。

3. **不复用 `ISignatureVerifier`, 自建脚本签名机制**: 被否决。理由: ADR-0039 已建立成熟的 Ed25519 校验实现, 重复造轮子无收益。

4. **Machine scope 用环境变量而非注册表 / 配置文件**: 被否决。理由: 环境变量易被用户修改, 不满足企业策略的强约束需求。注册表 (Windows) / `/etc` (Unix) 是 OS 级配置的标准位置。

5. **不支持 `# ExecutionPolicy:` pragma**: 被否决。理由: pragma 提供脚本作者声明意图的渠道, 在 CI/CD 场景下帮助用户理解脚本需求, 成本低收益明确。

6. **远程检测用文件扩展名 `.downloaded`**: 被否决。理由: 浏览器下载不一定改扩展名, 用户重命名后失效。ADS / xattr 是 OS 级标记, 更可靠。

## Open Questions

1. **签名密钥分发**: 是否需要内置可信公钥列表 (类似 GPG keyring)? 当前设计用户自行管理公钥, 企业场景需配套密钥分发机制。可能需独立 ADR。

2. **`AllSigned` 级别未来引入**: 若企业需求强烈, 是否在 v2 引入 `AllSigned`? 需评估与 `RemoteSigned` 默认值的过渡方案。

3. **签名格式标准化**: 当前设计 `.osh.sig` + `.osh.pub` 旁路文件, 是否需要内嵌签名 (如脚本头部 `# Signature: <base64>`)? 内嵌签名便于单文件分发但破坏可读性。

4. **远程检测的 macOS quarantine 属性**: `com.apple.quarantine` 在用户手动 `xattr -d` 后失效, 是否需要补充其他检测手段 (如 GateKeeper API)? 需 macOS 实现时评估。

5. **ExecutionPolicy 与 ADR-0049 WhatIf 的交互**: ADR-0049 §44 提到 "受限策略下强制 `-WhatIf`"。本 ADR 暂未实现此交互, 留待后续迭代。

## References

- PowerShell about_Execution_Policies: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies
- PowerShell Set-ExecutionPolicy: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-executionpolicy
- NTFS Alternate Data Streams: https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c0b8e1c5-b4ab-4f7a-a04d-9bb18f6a56b6
- Linux extended attributes (xattr): https://man7.org/linux/man-pages/man7/xattr.7.html
- macOS quarantine attribute: https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution
- ADR-0036 Security Sandbox
- ADR-0039 Provider Package Ecosystem (§8 Ed25519 签名)
- ADR-0046 Script Blocks (§621/§706 ExecutionPolicy 引用)
- ADR-0049 ShouldProcess (§44 ExecutionPolicy 交互)
- ADR-0022 Configuration Persistence
