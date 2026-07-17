# ADR-0049: ShouldProcess / -WhatIf / -Confirm 通用参数体系与 CmdletBinding

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0007 (Operation Engine), ADR-0023 (Catalog), ADR-0024 (Functions), ADR-0026 (Error Model), ADR-0036 (Security), ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0048 (Cmdlets)

## Context

PowerShell 的 `SupportsShouldProcess` 是破坏性操作安全网的核心机制：它让 `Remove-Item` / `Move-Item` / `Stop-Process` 等命令在执行前先经 `ShouldProcess` 把关，通过 `-WhatIf` 做干跑（dry-run）、通过 `-Confirm` 做逐项确认。这套机制是 PowerShell 与 Unix shell 在"安全 shell"维度上的关键差异——`rm -rf /` 不会问，但 `Remove-Item -Recurse -Force` 默认会按 `ConfirmImpact` 决定是否提示。

OpenShell 当前（M2 结束、M4 语言层推进中）完全没有 `ShouldProcess` 机制：

- ADR-0023 列出的破坏性命令（`Remove-Item` / `Move-Item` / `Set-Content` / `Clear-Content` / `Copy-Item` / `Stop-Process` 等）执行时**无任何用户确认环节**，`rm -rf` 等价的 `Remove-Item -Recurse -Force` 直接调用 `IOperationEngine.DeleteAsync`（见 `src/OpenShell.Core/Commands/Builtins/RemoveItemCommand.cs`）
- ADR-0026 错误模型定义了 `OperationCancelled` 错误分类，但没有"用户取消"的入口（`ShouldProcess` 返回 `false` 时本应产生此分类）
- ADR-0036 安全沙箱提供了 `RiskAnalyzer` 把操作分级为 Safe / Low / Medium / High / Critical / Destructive，但这是**内部风险评估**，没有暴露给用户的 `-WhatIf` / `-Confirm` 入口；RiskAnalyzer 的分级结果目前只用于审计与沙箱拦截，未驱动交互确认
- ADR-0048（Cmdlets）要求所有 cmdlet 暴露通用参数，但通用参数中与安全相关的 `-WhatIf` / `-Confirm` 子集需要本 ADR 定义其语义
- ADR-0046 脚本块在 §8 `param()` 块中已预留"通用参数（`-Verbose` / `-Debug` / `-WhatIf` / `-Confirm` per ADR-0049）自动可用"的钩子，需本 ADR 兑现

### 痛点

1. **破坏性操作无安全网**：`Remove-Item fs::C:/Important` 直接执行，无确认；`Move-Item` 覆盖目标无提示
2. **无法干跑**：运维脚本上线前无法用 `-WhatIf` 预演，必须真删真改才能验证
3. **PowerShell 兼容性阻断**：每一个触及生产数据的 PowerShell 脚本都依赖 `-WhatIf` 做干跑校验（`Remove-OldBackups -WhatIf` 是上线前标准动作），OpenShell 不支持则此类脚本无法迁移
4. **批量操作失控**：`Get-ChildItem | Remove-Item` 这种管道批量删除无逐项确认，误操作无法挽回
5. **RiskAnalyzer 分级无出口**：ADR-0036 已经把操作分级，但分级结果未驱动任何用户可见行为（既不提示也不阻断），投资浪费
6. **GUI 与 CLI 行为不一致**：GUI 有"操作确认对话框"的视觉位置，但 CLI 无对应机制，两端语义割裂

### 依赖关系

- **上游依赖**（本 ADR 依赖以下 ADR）：
  - ADR-0007（Operation Engine）：`ShouldProcess` 返回 `true` 后才调用 `IOperationEngine.DeleteAsync` 等
  - ADR-0023（Catalog）：`[Verb]` 特性定义命令元数据，本 ADR 在其上叠加 `[SupportsShouldProcess]`
  - ADR-0026（Error Model）：用户在 `-Confirm` 提示选 `N` 时产生 `OperationCancelled` 错误记录
  - ADR-0036（Security）：`RiskAnalyzer` 分级作为 `ConfirmImpact` 默认值的来源
  - ADR-0046（Script Blocks）：`param()` 块中 `[CmdletBinding]` 特性的求值
  - ADR-0047（Variable System）：`$WhatIfPreference` / `$ConfirmPreference` / `$PSCmdlet` 自动变量的作用域与生命周期
  - ADR-0048（Cmdlets）：本 ADR 是其通用参数体系的子集定义
- **下游依赖**（本 ADR 被以下 ADR / 模块依赖）：
  - 所有声明 `[SupportsShouldProcess]` 的内置命令（见 §7 清单）
  - ADR-0044（GUI Progress UI）：`-Confirm` 弹窗与 `Suspend` 嵌套 REPL 的 GUI 表现
  - 未来 ADR（Execution Policy）：`-WhatIf` 与执行策略的交互（受限策略下强制 `-WhatIf`）

### 设计原则

1. **PowerShell 全兼容**：`-WhatIf` / `-Confirm` 行为、`ShouldProcess` / `ShouldContinue` 签名、`Y/A/N/L/S/?` 提示格式、`$WhatIfPreference` / `$ConfirmPreference` 自动变量全部对齐 PowerShell 5.1+
2. **安全责任分层**：`ShouldProcess` 是"用户确认层"，ADR-0036 是"沙箱拦截层"，二者正交——沙箱拦截 Critical/Destructive 操作不可被 `-Confirm` 绕过，`-WhatIf` 也无法预演被沙箱禁止的操作
3. **复用 RiskAnalyzer**：不重新发明操作分级，ADR-0036 的 `RiskAnalyzer` 输出直接映射为 `ConfirmImpact` 默认值
4. **C# 与脚本对称**：C# 命令通过 `[SupportsShouldProcess]` 特性 + `CommandContext.ShouldProcess` 方法获得能力；脚本函数通过 `[CmdletBinding(SupportsShouldProcess)]` + `$PSCmdlet.ShouldProcess` 获得相同能力
5. **性能：无操作时零成本**：未传 `-WhatIf` / `-Confirm` 且 `ConfirmImpact` 低于阈值时，`ShouldProcess` 退化为单次布尔判断，不进入提示路径

## Decision

### 1. `CmdletBinding` 特性

脚本函数通过 `[CmdletBinding()]` 特性声明 cmdlet 级行为，与 PowerShell 完全对齐：

```powershell
function Remove-OldBackups {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [string]$Path,

        [int]$OlderThanDays = 30
    )

    process {
        if ($PSCmdlet.ShouldProcess($Path, "Delete backup older than $OlderThanDays days")) {
            Remove-Item $Path
        }
    }
}
```

`[CmdletBinding]` 接受以下命名参数：

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `SupportsShouldProcess` | bool | `$false` | 启用 `-WhatIf` 与 `-Confirm` 通用参数，激活 `$PSCmdlet.ShouldProcess` |
| `ConfirmImpact` | enum（None/Low/Medium/High） | `Medium` | 命令的默认影响等级，决定是否触发自动确认 |
| `SupportsPaging` | bool | `$false` | 启用 `-First` / `-Skip` / `-IncludeTotalCount` 分页参数（已实现，见 §1.1） |
| `SupportsTransactions` | bool | `$false` | 启用事务参数 `-UseTransaction`（已实现参数入口；事务系统本身待事务 ADR） |
| `DefaultParameterSetName` | string | `__AllParameterSets` | 当多个参数集歧义时使用的默认集名 |
| `PositionalBinding` | bool | `$true` | 是否允许位置参数绑定（设 `$false` 强制命名参数） |
| `HelpURI` | string | `""` | `Get-Help` 跳转的 URI |
| `RemotingCapability` | enum | `PowerShell` | 远程能力标记（已实现，见 `RemotingCapability` enum） |

对于 C# 命令，等价机制是在命令类上叠加 `[SupportsShouldProcess]` 特性（与 ADR-0023 的 `[Verb]` 特性并列，不替换）：

```csharp
[Verb("Remove", Noun = "Item", Aliases = ["rm", "del", "ri"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Removes an item, by default to the recycle bin.")]
public sealed class RemoveItemCommand : ICommand<RemoveItemCommand.Args>
{
    // ...
}
```

`[CmdletBinding]` 与 `[SupportsShouldProcess]` 是同一概念在两个入口（脚本 / C#）的镜像，运行时统一映射到 `CommandMetadata.SupportsShouldProcess` 与 `CommandMetadata.ConfirmImpact`。

#### 1.1 `SupportsPaging` 实现细节

当 `[CmdletBinding(SupportsPaging)]` 为 `$true` 时，运行时（`ScriptBlock.InjectCmdletBindingEnvironment`）自动注入：

- **通用参数**：`-First <UInt64>` / `-Skip <UInt64>` / `-IncludeTotalCount`（switch）
- **`$PSCmdlet.PagingParameters`**：`PagingParameters` 对象，含 `First` / `Skip` / `IncludeTotalCount` 三个属性
- **顶层变量**：`$First` / `$Skip` / `$IncludeTotalCount`（便于脚本直接访问）

命令实现应在枚举结果时按 `Skip` 跳过、按 `First` 限制数量，并在 `IncludeTotalCount` 为 true 时设置 `$TotalCount` 变量报告总数。

#### 1.2 `SupportsTransactions` 实现细节

当 `[CmdletBinding(SupportsTransactions)]` 为 `$true` 时，运行时自动注入：

- **通用参数**：`-UseTransaction`（switch）
- **`$PSCmdlet.UseTransaction`**：bool 属性
- **顶层变量**：`$UseTransaction`

事务系统本身（`TransactionScope` / `Commit` / `Rollback`）需要独立 ADR（批6），此处仅暴露参数入口与变量绑定。

### 2. `SupportsShouldProcess` 启用的通用参数与自动变量

当命令声明 `SupportsShouldProcess = $true` 时，运行时自动注入两个通用参数：

- **`-WhatIf`**（switch）— 不执行破坏性动作，仅打印"将做什么"。出现即设命令作用域内 `$WhatIfPreference = $true`
- **`-Confirm`**（switch）— 在每个破坏性动作前提示用户。出现即设命令作用域内 `$ConfirmPreference = $true`（等价于把阈值拉到最低）

同时暴露三个**偏好 / 上下文变量**（per ADR-0042 §3.8，非只读自动变量；per ADR-0047 作用域栈，命令调用时在 Local 作用域用 `Set` 写入，调用返回时 Local 帧出栈回收）：

| 变量 | 类型 | 含义 |
|---|---|---|
| `$WhatIfPreference` | bool | `$true` 表示处于 WhatIf 模式（`-WhatIf` 传入或全局默认为 `$true`） |
| `$ConfirmPreference` | enum（None/Low/Medium/High） | 当前确认阈值，默认 `$High`（仅 High 影响动作提示） |
| `$PSCmdlet` | `CmdletContext` | cmdlet 上下文对象，暴露 `ShouldProcess` / `ShouldContinue` / `WriteVerbose` 等（见 §8） |

全局默认值（存于 Global 作用域，per ADR-0047 §1.2）：

- `$WhatIfPreference = $false`（默认不干跑）
- `$ConfirmPreference = 'High'`（默认仅 High 影响动作提示，与 PowerShell 一致）

用户可在 profile 或 REPL 顶层覆盖：

```powershell
$ConfirmPreference = 'Medium'    # 此会话内 Medium 及以上影响动作都提示
$WhatIfPreference = $true          # 此会话内所有 SupportsShouldProcess 命令默认干跑
```

命令调用时 `-WhatIf` / `-Confirm` 传入的值覆盖全局默认（仅作用于本次调用，命令返回后恢复）。

### 3. `ShouldProcess` 方法

`ShouldProcess` 是 `SupportsShouldProcess` 命令的破坏性动作守门方法。三个重载：

```csharp
bool ShouldProcess(string target, string action)
bool ShouldProcess(string target)                                    // action 从 Verb 派生
bool ShouldProcess(string target, string action, string caption)    // 含弹窗标题
```

- `target`：操作对象的人类可读描述（如 `"C:/Users/backups/old.zip"`）
- `action`：将要执行的动作（如 `"Delete backup older than 30 days"`）；省略时从命令 Verb 派生（`Remove` → `"Remove"`）
- `caption`：弹窗标题（GUI 用），CLI 忽略

返回值：

- `true` — 继续执行（未处于 WhatIf 模式，且用户已确认或影响等级低于阈值）
- `false` — 跳过动作（处于 WhatIf 模式，或用户在确认提示中选 `N` / `L`）

#### 3.1 决策流程

`ShouldProcess(target, action)` 按以下顺序判断（任一命中即返回）：

1. **WhatIf 检查**：若 `$WhatIfPreference` 为 `$true`：
   - 向 host 写一行：`What if: Performing the operation '<action>' on target '<target>'.`
   - 返回 `false`（不执行动作）
2. **ConfirmPreference = None 检查**：若 `$ConfirmPreference` 为 `None`：
   - 返回 `true`（用户已显式禁用所有确认）
3. **影响等级比较**：若命令 `ConfirmImpact` ≥ `$ConfirmPreference`（None < Low < Medium < High 的偏序，`≥` 即"更严重或同等"）：
   - 进入交互确认提示（见 §3.2）
4. **影响等级低于阈值**：返回 `true`（无需确认）

#### 3.2 交互确认提示

确认提示格式与 PowerShell 完全一致：

```
Confirm
Are you sure you want to perform this action?
Performing the operation '<action>' on target '<target>'.
[Y] Yes  [A] Yes to All  [N] No  [L] No to All  [S] Suspend  [?] Help (default is "Y"):
```

按键映射：

| 键 | 行为 | 返回值 |
|---|---|---|
| `Y` | 确认本次动作 | `true` |
| `A` | 确认本次及本会话所有后续动作（设会话级 `YesToAll = true`） | `true` |
| `N` | 拒绝本次动作 | `false` |
| `L` | 拒绝本次及本会话所有后续动作（设会话级 `NoToAll = true`） | `false` |
| `S` | 挂起：进入嵌套 REPL（per ADR-0008），`exit` 返回后重新弹出本提示 | 重新提示 |
| `?` | 显示帮助文本（解释每个键），重新提示 | 重新提示 |
| 回车 | 默认值（`Y`，括号内 `(default is "Y")`） | `true` |

会话级 `YesToAll` / `NoToAll` 标志存于 `CommandContext`，命令调用范围内有效（命令返回后清零，与 PowerShell 一致——PowerShell 的 YesToAll 是 cmdlet 实例级，OpenShell 等价为命令调用级）。

#### 3.3 派生 action 的规则

当调用 `ShouldProcess(target)` 省略 `action` 时：

- 从命令的 `[Verb]` 特性取 Verb 字符串（如 `Remove` / `Set` / `Stop`）
- 派生为动词原形：`Remove` → `"Remove"`、`Set` → `"Set"`、`Stop` → `"Stop"`
- 派生结果作为 `action` 用于 WhatIf 输出与确认提示

### 4. `ShouldContinue` 方法

`ShouldContinue` 是不受 `ConfirmImpact` 控制的"强制确认"方法，用于：

- 二次确认（如 `Remove-Item -Recurse -Force` 已通过 `ShouldProcess` 后，再问"真的要强制递归删除吗"）
- 不声明 `SupportsShouldProcess` 但仍需提示的场景（罕见，破坏 PS 兼容惯例）

两个重载：

```csharp
bool ShouldContinue(string target, string action)

bool ShouldContinue(
    string query,
    string caption,
    string captionForYesToAll,
    string captionForNoToAll,
    ref bool yesToAll,
    ref bool noToAll)
```

行为差异（相对 `ShouldProcess`）：

| 维度 | `ShouldProcess` | `ShouldContinue` |
|---|---|---|
| 受 `-WhatIf` 影响 | 是（WhatIf 时返回 `false` 且不提示） | **否**（仍提示；但 WhatIf 模式下通常上层已 `return`，不会到达此处） |
| 受 `ConfirmImpact` 影响 | 是（按阈值决定是否提示） | **否**（总是提示） |
| 受 `-Confirm` 影响 | 是 | 否（与 `-Confirm` 无关） |
| 典型用途 | 破坏性动作主守门 | 二次确认 / 无 impact 分级时的提示 |

`ShouldContinue` 不读 `$WhatIfPreference` / `$ConfirmPreference`，调用方需自行决定何时调用。`yesToAll` / `noToAll` 通过 `ref` 传出，调用方负责在多次调用间传递状态。

### 5. `ConfirmImpact` 等级

`ConfirmImpact` 是命令破坏性的静态分级，与运行时 `$ConfirmPreference` 阈值比较决定是否自动提示。

| 等级 | 含义 | 触发提示的 `$ConfirmPreference` |
|---|---|---|
| `None` | 永不自动确认 | 永不（除非 `-Confirm` 显式传入） |
| `Low` | 轻度影响（覆盖文件、改配置） | `Low` / `Medium` / `High` |
| `Medium` | 中度影响（删除单文件、清空内容） | `Medium` / `High` |
| `High` | 高度影响（强制递归删除、杀进程、格式化） | `High` |

偏序：`None < Low < Medium < High`。触发条件：`ConfirmImpact ≥ $ConfirmPreference` 且二者都不为 `None`。

典型命令分级（与 PowerShell 默认对齐）：

| 命令 | `ConfirmImpact` | 理由 |
|---|---|---|
| `Get-ChildItem` | None | 只读 |
| `Get-Content` | None | 只读 |
| `Set-Location` | None | 无副作用 |
| `Set-Content` | Low | 覆盖文件内容 |
| `Copy-Item` | Low | 覆盖目标文件 |
| `Move-Item` | Low | 移动（覆盖目标） |
| `New-Item` | None | 创建（非破坏） |
| `Remove-Item` | Medium | 删除（默认走 Trash） |
| `Remove-Item -Recurse` | Medium | 递归删除 |
| `Remove-Item -Recurse -Force` | High | 强制递归删除（绕过 Trash） |
| `Clear-Content` | Medium | 清空文件内容 |
| `Stop-Process` | High | 杀进程 |
| `Stop-Service` | High | 停服务 |
| `Restart-Service` | High | 重启服务 |
| `Clear-History` | Medium | 清历史 |
| `Remove-Variable` | Low | 删变量 |
| `Remove-PSDrive` | Medium | 卸载 Drive |
| `Remove-Item env:*` | Medium | 删环境变量 |
| `Uninstall-Provider` | High | 卸载 Provider |
| `Rollback-Update` | High | 回滚更新 |
| `Format-C:`（假设） | High | 格式化驱动器 |

`-Recurse` / `-Force` 等参数可在运行时动态抬升 `ConfirmImpact`（`Remove-Item` 默认 Medium，传 `-Force` 抬升为 High），由命令实现内部调用 `ShouldProcess` 前根据参数计算实际 impact。

### 6. 与 ADR-0036 风险分析的集成

ADR-0036 的 `RiskAnalyzer` 已对操作做动态分级（Safe / Low / Medium / High / Critical / Destructive），本 ADR 把它作为 `ConfirmImpact` 默认值的来源：

| RiskAnalyzer 分级 | 映射 `ConfirmImpact` | 额外行为 |
|---|---|---|
| Safe | None | 无 |
| Low | Low | 无 |
| Medium | Medium | 无 |
| High | High | 无 |
| Critical | High | **需 `-Force` 显式授权**，`-Confirm` 单独不足以放行 |
| Destructive | High | **需 `-Force` 显式授权**，且 `-Confirm` 提示中无 `A`（Yes to All）选项，每项必须单独确认 |

集成规则：

1. 命令未在 `[CmdletBinding]` / `[SupportsShouldProcess]` 显式声明 `ConfirmImpact` 时，调用 `RiskAnalyzer.Classify(operation)` 取动态分级作为默认 `ConfirmImpact`
2. 显式声明优先于 `RiskAnalyzer`（命令作者明确指定则尊重其判断）
3. `RiskAnalyzer` 分级为 `Critical` / `Destructive` 时，**即使 `ShouldProcess` 返回 `true`，仍需检查 `-Force` 参数**：未传 `-Force` 则抛 `ErrorCategory.PermissionDenied`（per ADR-0026），错误信息含"此操作分级为 Critical/Destructive，需 -Force 授权"
4. `RiskAnalyzer` 的分级可能基于运行时上下文（如目标路径在 `C:/Windows` 下自动抬升一级），因此每次 `ShouldProcess` 调用都重新查询，不缓存

```csharp
// RemoveItemCommand 内部
var risk = ctx.RiskAnalyzer.Classify(path, DeleteOptions { Recurse = args.Recurse, Force = args.Force });
var effectiveImpact = args.Force ? ConfirmImpact.High : MapRiskToImpact(risk);
if (!ctx.ShouldProcess(path.Display, "Remove", effectiveImpact))
    yield break;   // WhatIf 或用户拒绝

if (risk is RiskLevel.Critical or RiskLevel.Destructive && !args.Force)
{
    ctx.Errors?.Write(new ErrorRecord { Category = ErrorCategory.PermissionDenied, ... });
    yield break;
}
```

### 7. 现有命令的 `[SupportsShouldProcess]` 更新

以下内置命令（per ADR-0023 清单）须添加 `[SupportsShouldProcess]` 并在执行破坏性动作前调用 `ShouldProcess`：

| 命令 | `ConfirmImpact`（默认） | 动态抬升条件 |
|---|---|---|
| `Remove-Item` | Medium | `-Recurse -Force` → High |
| `Move-Item` | Low | 覆盖已存在目标 → Medium |
| `Set-Content` | Low | — |
| `Clear-Content` | Medium | — |
| `Copy-Item` | Low | 覆盖已存在目标 → Medium |
| `Stop-Process` | High | — |
| `Stop-Service` | High | — |
| `Restart-Service` | High | — |
| `Clear-History` | Medium | — |
| `Remove-Variable` | Low | — |
| `Remove-PSDrive` | Medium | — |
| `Remove-Item env:*` | Medium | — |
| `Uninstall-Provider` | High | — |
| `Rollback-Update` | High | — |

更新模式（以 `RemoveItemCommand` 为例）：

```csharp
[Verb("Remove", Noun = "Item", Aliases = ["rm", "del", "ri"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Removes an item, by default to the recycle bin.")]
public sealed class RemoveItemCommand : ICommand<RemoveItemCommand.Args>
{
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var engine = ctx.Operations;
        if (engine is null) { /* ... 错误处理同原 ... */ yield break; }

        var path = ResolvePath(args.Path, ctx);
        var action = args.Force ? "physically remove" : "remove (to recycle bin)";
        var impact = args.Recurse && args.Force ? ConfirmImpact.High : ConfirmImpact.Medium;

        if (!ctx.ShouldProcess(path.Display, action, impact))
            yield break;   // WhatIt 或用户拒绝，无 ErrorRecord（用户取消非错误）

        // ... 原 DeleteAsync 调用 ...
    }
}
```

未在清单中的破坏性命令（如未来新增）默认必须声明 `[SupportsShouldProcess]`，由代码审查与 lint 强制（见约束）。

### 8. `$PSCmdlet` 自动变量

在声明 `[CmdletBinding]` 的脚本函数 / 脚本块体内，`$PSCmdlet` 自动变量暴露 cmdlet 上下文（per ADR-0047 自动变量，命令调用时在 Local 作用域写入）：

| 成员 | 签名 | 说明 |
|---|---|---|
| `ShouldProcess` | `(string target, string action) → bool` | 见 §3 |
| `ShouldProcess` | `(string target) → bool` | action 从 Verb 派生 |
| `ShouldProcess` | `(string target, string action, string caption) → bool` | 含 caption |
| `ShouldContinue` | `(string target, string action) → bool` | 见 §4 |
| `ShouldContinue` | `(string query, string caption, string captionForYesToAll, string captionForNoToAll, ref bool yesToAll, ref bool noToAll) → bool` | 含 YesToAll / NoToAll |
| `WriteVerbose` | `(string text) → void` | 写 Verbose 流（受 `-Verbose` 控制） |
| `WriteWarning` | `(string text) → void` | 写 Warning 流 |
| `WriteDebug` | `(string text) → void` | 写 Debug 流（受 `-Debug` 控制） |
| `WriteError` | `(ErrorRecord errorRecord) → void` | 写错误流（per ADR-0026） |
| `WriteProgress` | `(ProgressRecord progressRecord) → void` | 写进度（per ADR-0044） |
| `WriteInformation` | `(string message) → void` | 写 Information 流 |
| `Invoke` | `() → void` | 调用另一个 cmdlet（罕见，本 ADR 不展开） |
| `MyInvocation` | `InvocationInfo` | 调用信息（命令名、脚本位置、参数） |
| `ParameterSetName` | `string` | 当前激活的参数集名 |

对于 C# 命令，`CommandContext`（已有 `Errors` / `Output` / `Host` 等成员）扩展以下方法：

```csharp
public sealed class CommandContext
{
    // 已有：Errors, Output, Host, Operations, CurrentLocation, ...

    public bool ShouldProcess(string target, string action, ConfirmImpact? impact = null);
    public bool ShouldProcess(string target);
    public bool ShouldContinue(string target, string action);
    public bool ShouldContinue(string query, string caption, string captionForYesToAll,
                               string captionForNoToAll, ref bool yesToAll, ref bool noToAll);

    public bool WhatIfPreference { get; }      // 从 -WhatIf 或全局 $WhatIfPreference
    public ConfirmImpact ConfirmPreference { get; }   // 从 -Confirm 或全局 $ConfirmPreference
    public ConfirmImpact DeclaredConfirmImpact { get; }  // 从 [SupportsShouldProcess] 声明
}
```

`CommandContext.ShouldProcess` 内部查询 `$WhatIfPreference` / `$ConfirmPreference`（或 host 的交互提示能力），实现与脚本侧 `$PSCmdlet.ShouldProcess` 完全一致的行为。

### 9. CLI Host 集成

CLI host（per ADR-0008）实现 `IHostInteraction` 的确认与 WhatIf 接口：

- **WhatIf 模式**：`ShouldProcess` 内部当 `$WhatIfPreference = $true` 时，调用 `host.WriteOutputLine("What if: ...")` 输出到标准输出（不写到错误流，因为这不是错误），然后返回 `false`
- **Confirm 提示**：使用 `Console.ReadLine()` 读取用户输入，按 §3.2 的键映射处理；不区分大小写（`y` / `Y` 等价）
  - 默认值高亮：提示字符串中 `(default is "Y")` 的大写字母即默认值
  - 空输入（直接回车）走默认值
  - 非法输入（非 `Y/A/N/L/S/?`）重新提示
- **Suspend**：进入嵌套 REPL（per ADR-0008 §nested REPL），提示符变为 `>>`，用户输入 `exit` 返回，重新弹出原确认提示
- **非交互 host**（如管道 / 重定向输入）：`-Confirm` 提示无法读取用户输入时，按默认值处理（`Y`），并写一条 Warning 提示"非交互模式，按默认 Y 处理"；可通过 `$ConfirmPreference = 'None'` 或 `-Confirm:$false` 显式跳过

### 10. GUI Host 集成

GUI host（per ADR-0013 MVVM / ADR-0043 对话框服务 / ADR-0044 进度 UI）：

- **WhatIf 模式**：动作描述写入任务中心（per ADR-0044 Task Center），标记为"干跑预览"样式（灰色 / 虚线图标），不执行实际操作
- **Confirm 提示**：弹出模态对话框（per ADR-0043 `IDialogService`），含按钮：
  - `Yes` / `Yes to All` / `No` / `No to All` / `Suspend` / `Help`
  - 默认按钮高亮（与 CLI 默认值一致）
  - `Enter` 触发默认按钮，`Esc` 等价 `No`
- **Suspend**：在对话框内嵌入嵌套 REPL 输入框（GUI 实现 TBD，当前降级为"取消本次操作"等价 `N`，写 Warning 提示"GUI 暂不支持 Suspend，已取消"；CLI 已实现完整嵌套 REPL）
- **非交互场景**（GUI 后台任务 / 自动化）：同 CLI 非交互处理

GUI 与 CLI 共用 `CommandContext.ShouldProcess` 实现，差异仅在 host 的提示渲染层（CLI 走 `Console.ReadLine`，GUI 走 `IDialogService.Confirm`）。

### 11. 通用参数完整清单

per ADR-0048，所有 cmdlet（无论是否 `SupportsShouldProcess`）都暴露以下通用参数。本 ADR 固化其语义：

#### 11.1 所有 cmdlet 通用

| 参数 | 类型 | 说明 |
|---|---|---|
| `-Verbose` | switch | 设 `$VerbosePreference = 'Continue'`（命令作用域内），触发 `WriteVerbose` 输出 |
| `-Debug` | switch | 设 `$DebugPreference = 'Continue'`，触发 `WriteDebug` 输出 |
| `-ErrorAction` | enum | 控制非终止错误处理：`SilentlyContinue` / `Stop` / `Continue`（默认）/ `Inquire` / `Ignore` / `Suspend` |
| `-WarningAction` | enum | 同 `-ErrorAction`，作用于 Warning 流 |
| `-InformationAction` | enum | 同上，作用于 Information 流 |
| `-ErrorVariable` | string | 把 ErrorRecord 追加到命名变量（如 `-ErrorVariable errs` 后 `$errs` 是数组） |
| `-WarningVariable` | string | 同上，Warning |
| `-InformationVariable` | string | 同上，Information |
| `-OutVariable` | string | 把成功输出捕获到命名变量（同时仍流入管道） |
| `-OutBuffer` | int | 缓冲 N 个对象后再传下游（管道节流） |
| `-PipelineVariable` | string（别名 `-PV`） | 把当前管道项存入命名变量（在本次迭代内可见） |

`-ErrorAction` 各值行为（per ADR-0026 §13）：

- `Continue`（默认）：非终止错误写入错误流，命令继续
- `Stop`：非终止错误升级为终止错误（抛 `OpenShellException`）
- `SilentlyContinue`：非终止错误静默跳过（不写错误流）
- `Ignore`：同 `SilentlyContinue`，且不写入 `$Error` 自动变量
- `Inquire`：非终止错误时弹出确认提示（复用 §3.2 提示）
- `Suspend`：挂起命令（进入嵌套 REPL），用户排查后可恢复（已实现，复用 `IConfirmationPrompter.SuspendCallback` 嵌套 REPL 机制）

#### 11.2 仅 `SupportsShouldProcess` 时附加

| 参数 | 类型 | 说明 |
|---|---|---|
| `-WhatIf` | switch | 设 `$WhatIfPreference = $true`（命令作用域内） |
| `-Confirm` | switch | 设 `$ConfirmPreference = $true`（等价拉到最低阈值，所有 impact 都提示） |

通用参数大小写不敏感（`-whatif` / `-WhatIf` / `-WHATIF` 等价），与命令参数命名规则一致（per ADR-0023）。

通用参数与命令自身参数同名时冲突报 `ParameterBindingException`（命令作者不应声明与通用参数同名的参数）。

## Alternatives Considered

1. **不做 ShouldProcess（M1-M2 现状）**：被否决。理由：阻断 PowerShell 全兼容目标（每个触生产数据的 PS 脚本都依赖 `-WhatIf` 干跑）；破坏性操作无安全网，`rm -rf` 等价的 `Remove-Item -Recurse -Force` 直接执行不可挽回；ADR-0036 的 `RiskAnalyzer` 分级无用户可见出口，投资浪费。

2. **破坏性命令强制提示（无 `-Confirm` opt-out）**：被否决。理由：破坏自动化——CI/CD 脚本 `Remove-Item $buildArtifacts` 每次都要人工确认，无法无人值守；与 PowerShell 行为不符（PS 默认 `$ConfirmPreference = 'High'`，仅 High 影响 prompt，Low/Medium 静默）；用户体验烦扰，导致用户习惯性 `-Confirm:$false` 绕过，反而失去安全网。

3. **自定义确认体系（非 PowerShell 风格）**：被否决。理由：PS 脚本迁移成本——用户已熟悉 `Y/A/N/L/S/?` 提示与 `$ConfirmPreference` 阈值模型，自创体系需重新学习；`ShouldProcess` API 形状（返回 bool、调用方 `if` 包裹）是 PS cmdlet 作者的肌肉记忆，改动则所有迁移的 cmdlet 都需重写确认逻辑。

4. **嵌入 PowerShell 的 ShouldProcess 实现（引用 `System.Management.Automation`）**：被否决。理由：SMA 体积大（~30MB）、与 Windows PowerShell / .NET Framework 耦合；其 `Cmdlet.ShouldProcess` 与 `CommandRuntime` 深度绑定，需大量桥接；OpenShell 的 `IItem` / `ItemPath` / `CommandContext` 模型与 SMA 的 `Cmdlet` / `InvocationInfo` 不直接兼容。改为"同 API、自实现"——签名与行为对齐 PowerShell，实现基于 OpenShell 自有 `CommandContext` 与 host 交互层。

5. **仅 C# 命令支持，脚本函数不支持 `ShouldProcess`**：被否决。理由：脚本函数是用户自定义破坏性命令的主要载体（`Remove-OldBackups` / `Clean-TempFiles` 等），不支持则用户无法在脚本中复用安全网；与 ADR-0024 revised 高级函数 / ADR-0046 脚本块的"PowerShell 全兼容"目标冲突；`[CmdletBinding]` 是 PS 高级函数的标志特性，缺失则高级函数名不副实。

6. **`-WhatIf` 仅打印命令行不调用 `ShouldProcess`**：被否决。理由：`ShouldProcess` 返回 `false` 是命令内部决定跳过动作的契约，单纯在命令行层打印无法阻止命令执行破坏性代码；必须由命令实现内 `if ($PSCmdlet.ShouldProcess(...))` 包裹动作，因此 `-WhatIf` 的语义必须经 `ShouldProcess` 方法传递。

7. **`ConfirmImpact` 与 RiskAnalyzer 分级独立（不集成）**：被否决。理由：双套分级体系（命令静态 `ConfirmImpact` + RiskAnalyzer 动态分级）语义重复、维护成本高、易不一致；本 ADR 让 RiskAnalyzer 作为默认值来源、命令显式声明优先，避免重复发明且保留命令作者覆盖权。

## Consequences

### 优势

- **破坏性操作安全网**：`Remove-Item` / `Move-Item` / `Stop-Process` 等命令在执行前经 `ShouldProcess` 把关，用户可通过 `-Confirm` 逐项确认、`-WhatIf` 干跑预演
- **PowerShell 全兼容**：`-WhatIf` / `-Confirm` / `$WhatIfPreference` / `$ConfirmPreference` / `$PSCmdlet.ShouldProcess` 行为与签名对齐 PowerShell 5.1+，PS 脚本零成本迁移
- **干跑能力**：运维脚本上线前 `Remove-OldBackups -WhatIf` 预演，避免误删生产数据
- **RiskAnalyzer 投资兑现**：ADR-0036 的操作分级首次驱动用户可见行为（自动确认提示），分级投资不再浪费
- **CLI / GUI 行为统一**：`CommandContext.ShouldProcess` 单一实现，差异仅在 host 渲染层，两端语义一致
- **自动化友好**：默认 `$ConfirmPreference = 'High'` 仅 High 影响提示，Low/Medium 静默执行，CI/CD 脚本无人值守不被打断
- **与错误模型对齐**：用户拒绝（`N` / `L`）不产生 ErrorRecord（用户取消非错误），与 ADR-0026 `OperationCancelled` 语义一致（仅在命令显式选择把取消当错误时才写）

### 代价

- **开发者负担**：每个破坏性命令必须调用 `ShouldProcess` 包裹动作，遗漏则安全网失效；需代码审查与 lint 强制
- **实现量**：`[SupportsShouldProcess]` 特性、`CommandContext.ShouldProcess` / `ShouldContinue` 方法、`$PSCmdlet` 自动变量注入、CLI 确认提示、GUI 对话框集成约 1500 行 C# 代码
- **性能开销**：`ShouldProcess` 在未传 `-WhatIf` / `-Confirm` 且 impact 低于阈值时为单次布尔判断（< 1μs），可忽略；但传 `-Confirm` 进入提示路径时阻塞等待用户输入，管道批量操作可能显著变慢（这是预期行为）
- **`-WhatIf` 必须诚实实现**：命令作者必须确保 `ShouldProcess` 返回 `false` 时**完全不产生副作用**——不能"先部分执行再跳过"，否则 `-WhatIf` 失去干跑意义；这是约束而非自动保证
- **嵌套 REPL 复杂度**：`Suspend` 选项进入嵌套 REPL，host 须支持栈式 REPL 状态（per ADR-0008），GUI 的 Suspend 实现更复杂（M4 暂降级）
- **RiskAnalyzer 动态分级开销**：每次 `ShouldProcess` 调用都查询 `RiskAnalyzer.Classify`，高频管道（`Get-ChildItem | Remove-Item`）可能累积；可通过 RiskAnalyzer 内部分级缓存缓解

### 性能

- **无操作路径**（无 `-WhatIf` / `-Confirm`，impact 低于阈值）：`ShouldProcess` 退化为 `if (WhatIfPreference) ... else if (ConfirmPreference == None) ... else if (impact < ConfirmPreference) return true`，约 0.5μs
- **WhatIf 路径**：一次 `host.WriteOutputLine` 调用，约 5-10μs（取决于字符串长度）
- **Confirm 路径**：阻塞在 `Console.ReadLine` / `IDialogService.Confirm`，等待用户输入，时长由用户决定
- **RiskAnalyzer 查询**：典型 < 5μs（路径模式匹配 + 上下文检查），Critical/Destructive 分级可能涉及更多检查

### 约束

- **所有破坏性命令必须声明 `[SupportsShouldProcess]`**：per ADR-0036 `RiskAnalyzer` 分级 High+ 的命令必须调用 `ShouldProcess`；由代码审查与 lint 规则强制（lint 检测 `[Verb]` 为 `Remove` / `Stop` / `Clear` / `Set`（写场景）/ `Move`（覆盖场景）/ `Copy`（覆盖场景）但未声明 `[SupportsShouldProcess]` 的命令）
- **`-WhatIf` 必须诚实**：`ShouldProcess` 返回 `false` 时禁止任何副作用（不写文件、不删项、不发网络请求、不杀进程）；违反此约束是安全漏洞
- **`-Confirm` 提示格式精确匹配 PowerShell**：`Y/A/N/L/S/?` 六选项、`(default is "Y")` 默认值标注、`Confirm` 标题行，与 PowerShell 5.1+ 完全一致（PS 用户迁移无认知成本）
- **默认 `$ConfirmPreference = 'High'`、`$WhatIfPreference = $false`**：与 PowerShell 默认一致，禁止改动默认值（改动则破坏自动化兼容性假设）
- **通用参数大小写不敏感**：`-whatif` / `-WhatIf` / `-WHATIF` 等价
- **`ShouldProcess` 返回 bool，不调用即隐式接受所有风险**：命令未调用 `ShouldProcess` 而执行破坏性动作，视为命令作者明确放弃安全网（lint 应警告但非错误，因部分命令确需绕过）
- **`ShouldProcess` 不产生 ErrorRecord**：用户拒绝（`N` / `L`）时命令静默跳过该动作，不写错误流；若命令需把取消当错误，自行 `ctx.Errors.Write` 后 `return`
- **Critical / Destructive 操作不可被 `-Confirm` 单独放行**：必须 `-Force` 显式授权，`-Confirm` 的 `Y` / `A` 不足以绕过 RiskAnalyzer 的 Critical/Destructive 拦截
- **`[CmdletBinding]` 与 `[SupportsShouldProcess]` 镜像**：脚本侧 `[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]` 与 C# 侧 `[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]` 等价，运行时统一映射到 `CommandMetadata`
- **`$PSCmdlet` 仅在 `[CmdletBinding]` 函数 / 脚本块内可用**：普通函数（无 `[CmdletBinding]`）内 `$PSCmdlet` 为 `$null`；访问其成员抛 `RuntimeBinderException`
- **`-WhatIf` / `-Confirm` 仅 `SupportsShouldProcess` 命令可用**：未声明的命令传 `-WhatIf` 报 `ParameterBindingException`（未知参数）
- **会话级 `YesToAll` / `NoToAll` 命令调用范围内有效**：命令返回后清零，与 PowerShell cmdlet 实例级语义对齐（OpenShell 命令实例生命周期 = 一次调用）
- **`ShouldContinue` 不读 `$WhatIfPreference` / `$ConfirmPreference`**：调用方自行决定调用时机；WhatIf 模式下命令实现应在上层 `if (ShouldProcess(...))` 内调用 `ShouldContinue`，避免 WhatIf 时仍弹二次确认
- **RiskAnalyzer 分级不缓存于命令调用间**：动态分级可能基于运行时上下文（目标路径、参数组合），每次 `ShouldProcess` 重新查询
- **非交互 host 按默认值处理 `-Confirm`**：管道 / 重定向输入下 `Console.ReadLine` 不可用，按默认 `Y` 处理并写 Warning；用户可 `$ConfirmPreference = 'None'` 或 `-Confirm:$false` 显式跳过
- **通用参数禁止与命令参数同名**：命令声明 `-WhatIf` / `-Confirm` / `-Verbose` 等同名参数报 `ParameterBindingException`
