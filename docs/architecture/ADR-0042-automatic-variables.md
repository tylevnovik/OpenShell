# ADR-0042: 自动变量系统

- **Status**: Accepted (Revised)
- **Date**: 2026-07-07 (初版) / 2026-07-08 (修订)
- **Stage**: M2 → M3（修订扩展至 M3）
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (CLI REPL), ADR-0010 (Pipeline Object Stream), ADR-0024 (别名与函数), ADR-0026 (错误模型), ADR-0041 (Startup Script), ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0048 (Cmdlets), ADR-0049 (ShouldProcess)

> **修订说明（Revised 2026-07-08）**：本 ADR 经修订以支持 PowerShell 风格的类型化变量、多作用域修饰符、splatting、哈希/数组字面量、成员访问与子表达式。修订动因为「PowerShell 全兼容」方向决策——初版（2026-07-07）将 PowerShell 作用域栈简化为三层（Global/Script/Session）、值统一存 `object` 且显式拒绝 `$var.Property` / `$(...)` / `@{}` / `@()` / `private:` / `using:` / splatting / here-string，这些简化阻碍了 `.ps1` 脚本直接移植，故全部撤销。下文以「修订后」语义为准，初版被否决的方案见 §Alternatives Considered。

## Context

### 历史背景

M1 的 `CliHost.RunAsync` 里仅硬编码了两个变量查询：

```csharp
if (line is "$?") { ... }
if (line is "$LASTEXITCODE") { ... }
```

新增每个变量都要修改 ReplEngine，存在以下痛点：

1. **缺统一变量作用域与求值机制**：自动变量、用户变量、环境变量混在一起，无统一注册表
2. **扩展性差**：新增 `$PWD` / `$HOME` / `$ERROR` 等需要继续打补丁
3. **函数体无法参数化**：ADR-0024 的函数体内 `$args` / `$input` 无变量求值支撑
4. **环境变量访问繁琐**：当前需用户直接调用 `Environment.GetEnvironmentVariable`，无 shell 友好语法
5. **作用域语义缺失**：与 PowerShell 5 层作用域栈不对齐，函数调用栈帧无法表达
6. **插值无规则**：双引号字符串中无法嵌入变量与子表达式，函数体内 `"...$path..."` 无法展开

### 修订动因

初版 ADR-0042（2026-07-07）采用「三层静态作用域（Global/Script/Session）+ 值统一 `object` + 拒绝成员访问/子表达式/字面量/splatting」的简化设计。该设计在 M2 阶段可支撑基础 REPL 体验，但存在以下阻塞：

- **`.ps1` 脚本不可移植**：真实 PowerShell 脚本大量使用 `@{ }` 哈希、`[int]$x` 类型标注、`$_.Name` 成员访问、`@args` splatting、`$(...)` 子表达式、`@"..."@` here-string，初版全部拒绝
- **作用域模型不完整**：缺少 `private:` / `using:`，无法表达「私有变量不向子作用域泄漏」「远程/并行作用域传参」两类核心场景
- **类型语义缺失**：`[string[]]$names` / `[hashtable]$config` 等类型标注无法表达，运行时无类型强制
- **与 ADR-0046 Script Blocks / ADR-0048 Cmdlets 不联动**：脚本块作为一等公民需要闭包变量捕获，Cmdlet 的 `ShouldProcess` 需要 `$PSCmdlet` 上下文，初版均无支撑

经「PowerShell 全兼容」方向决策，本 ADR 修订为完整支持 PowerShell 变量语义。PowerShell 的变量作用域栈成熟且经过广泛验证，OpenShell 直接对齐以最大化脚本兼容性。

## Decision

### 1. 变量分类

OpenShell 变量分为四类：

| 类别 | 来源 | 读写 | 作用域 | 持久化 |
|---|---|---|---|---|
| 自动变量 | 系统提供 | 只读（核心系统可通过 `SetAutomatic` 写） | Global | 不持久化（运行时计算） |
| 用户变量 | 脚本/REPL 赋值 | 读写 | Local / Script / Global / Private | 默认不持久化；`Export-Variable` 可持久化 |
| 环境变量 | OS 桥接（`$env:` 前缀） | 读写 | Global（由 OS 维护） | 由 OS 维护 |
| 远程变量 | `using:` 修饰符传入 | 只读（在远程作用域内） | Using（远程作用域内） | 不持久化 |

### 2. 作用域模型（5 层 PowerShell 兼容）

修订后采用与 PowerShell 完全一致的 5 层作用域模型，替换初版的 3 层（Global/Script/Session）。初版的「Session」REPL 顶层并入 `global:`（PowerShell 中交互会话顶层即 global 作用域）。

| 修饰符 | 含义 | 生命周期 | 清理时机 |
|---|---|---|---|
| `global:` | 进程级全局作用域（交互会话顶层） | 进程生命周期 | 进程退出 |
| `script:` | 模块/脚本文件作用域 | 模块加载期间 | 模块卸载 / 脚本执行结束 |
| `local:` | 当前函数/脚本块作用域（**默认**） | 当前栈帧 | 函数/块返回 |
| `private:` | 仅当前作用域可见，**不向子作用域继承** | 当前栈帧 | 当前作用域退出 |
| `using:` | 远程/并行作用域传参（`Invoke-Command` / `Start-Job` / `Start-ThreadJob`） | 远程会话期间 | 远程会话结束 |

#### 2.1 作用域修饰符语法

变量名前缀显式指定作用域：

```powershell
$global:Var          # 显式访问/设置 global 作用域
$script:Var           # 显式访问/设置当前脚本文件作用域
$local:Var            # 显式访问/设置当前局部作用域（等同默认）
$private:Var          # 私有变量：仅当前作用域可见
$using:Var            # 在远程/并行作用域内读取外层捕获的变量
```

无修饰符的 `$Var` 默认为 `local:`（与 PowerShell 一致），查找规则见 §2.3。

#### 2.2 作用域栈

- 每次函数/脚本块调用**压入一个新的 local 作用域**到作用域栈
- 作用域栈是一个后进先出的栈，栈底为 `global:` 作用域
- 栈帧内可定义 `private:` 变量（不向子栈帧泄漏）
- `script:` 作用域与脚本文件加载绑定（每个 `.openshell` / `.osps1` 模块一个）
- `Get-Variable -Scope N` 访问第 N 个祖先作用域（0 = 当前，1 = 父，...）
- `Get-Variable -Scope Global` / `-Scope Script` / `-Scope Local` / `-Scope Private` 按名称访问

#### 2.3 变量查找规则

无修饰符 `$Var` 按以下顺序查找：

1. 当前 local 作用域（栈顶栈帧）
2. 向上逐层查找父作用域（local 栈帧链）
3. 当前 `script:` 作用域
4. `global:` 作用域（含自动变量）
5. 未命中抛 `VariableNotFoundException`（参考 §16）

`private:` 变量在第 1 步可见，但**不会**出现在第 2 步的向上查找中（即子作用域看不到父作用域的 private 变量）。

#### 2.4 `using:` 语义

`$using:Var` 仅在 `Invoke-Command -ComputerName` / `Start-Job` / `Start-ThreadJob` / `ForEach-Object -Parallel` 的脚本块内有效。语义：

- 在外层作用域求值 `Var`，将其值**按值复制**（深拷贝 for 引用类型）传到远程作用域
- 远程作用域内通过 `$using:Var` 读取
- 赋值 `$using:Var = ...` 在远程作用域内非法，抛 `InvalidOperationException`

### 3. 自动变量清单（扩展）

修订后自动变量清单扩展至与 PowerShell 对齐。所有自动变量只读（赋值抛 `ReadOnlyVariableException`），核心系统通过 `SetAutomatic` 更新。

> **偏好变量例外（per §3.8）**：`$WhatIfPreference` / `$ConfirmPreference` / `$VerbosePreference` / `$DebugPreference` / `$ErrorActionPreference` / `$PSCmdlet` 不属于只读自动变量。它们在 Global 框有默认值，用户可在 profile / REPL 顶层 `Set` 覆盖，`[CmdletBinding]` 命令调用时在 Local 作用域被 `Set` 覆盖，命令返回后弹出。详见 §3.8。

#### 3.1 执行状态

| 变量 | 类型 | 含义 |
|---|---|---|
| `$?` | bool | 上一条命令是否成功 |
| `$LASTEXITCODE` | int | 上一条原生/外部命令退出码（0/1/2/.../130） |
| `$Error` | `ErrorRecord[]` | 错误栈（`$Error[0]` 为最近错误，新错误 unshift 到头部） |
| `$ErrorActionPreference` | enum | `SilentlyContinue` / `Stop` / `Continue` / `Inquire`（默认 `Continue`） |

#### 3.2 环境信息

| 变量 | 类型 | 含义 |
|---|---|---|
| `$PWD` | ItemPath | 当前工作目录（等价 `CurrentLocation`，每次读取按需计算） |
| `$HOME` | string | 用户主目录（`Environment.GetFolderPath(UserProfile)`） |
| `$Host` | HostObject | **完整宿主对象**（含 `$Host.UI` / `$Host.Version` / `$Host.Name` 子对象），非字符串 |
| `$HOSTNAME` | string | 机器名（`Environment.MachineName`） |
| `$PID` | int | 当前进程 ID（`Environment.ProcessId`） |
| `$OS` | string | 操作系统：`"Windows"` / `"Linux"` / `"macOS"` |

> 注：`$Host` 修订为完整对象（初版为字符串 `"Cli"`/`"Gui"`）。原字符串值可通过 `$Host.Name` 获取。

#### 3.3 字面量

| 变量 | 类型 | 含义 |
|---|---|---|
| `$true` / `$TRUE` | bool | `true`（大小写不敏感） |
| `$false` / `$FALSE` | bool | `false`（大小写不敏感） |
| `$null` / `$NULL` | object | `null`（大小写不敏感） |

#### 3.4 函数/脚本块上下文

| 变量 | 类型 | 含义 |
|---|---|---|
| `$_` / `$PSItem` | object | 管道当前项（`process {}` 块内） |
| `$input` / `$INPUT` | `IEnumerator<IItem>` | 管道上游输入枚举器 |
| `$args` / `$ARGS` | `object[]` | 函数未绑定的位置参数 |
| `$PSBoundParameters` | hashtable | 显式绑定的命名参数字典 |
| `$MyInvocation` | InvocationInfo | 调用信息（脚本路径 / 行号 / 命令名 / 参数列表） |
| `$PSScriptRoot` | string | 当前脚本所在目录（脚本文件外为 `$null`） |
| `$PSCommandPath` | string | 当前脚本完整路径 |

#### 3.5 循环与匹配

| 变量 | 类型 | 含义 |
|---|---|---|
| `$foreach` | IEnumerator | `foreach` 循环枚举器 |
| `$switch` | IEnumerator | `switch` 语句枚举器 |
| `$matches` | hashtable | 最近一次 `-match` / `-notmatch` / `switch -Regex` 运算后的捕获组（键 `0` 为整体匹配，`1`/`2`/... 为分组） |

> 注：`$matches` / `$foreach` / `$switch` 由运行时在 Local 作用域自动 `Set` 更新，**非只读自动变量**（用户可覆盖，但通常只读）。匹配失败时 `$matches` 被置为 `$null`。

#### 3.6 Profile

`$PROFILE` 为完整多字段对象（非字符串）：

```powershell
$PROFILE.CurrentUserCurrentHost   # ~/.openshell/profile.openshell
$PROFILE.CurrentUserAllHosts      # ~/.openshell/profile.all.openshell
$PROFILE.AllUsersCurrentHost       # 全局当前 host profile
$PROFILE.AllUsersAllHosts          # 全局所有 host profile
```

#### 3.7 兼容别名

为兼容初版 ADR-0042，以下大写形式作为大小写不敏感别名保留：

| 别名 | 等价于 |
|---|---|
| `$ARGS` | `$args` |
| `$INPUT` | `$input` |
| `$ERROR` | `$Error[0]`（仅读取最近一条错误，向后兼容） |
| `$ERRORS` | `$Error`（完整错误栈） |

#### 3.8 偏好变量（preference variables，非只读）

偏好变量**不属于自动变量**，**可读写**（用户可通过 `Set` 赋值覆盖）。它们在 Global 框有默认值，`[CmdletBinding]` 命令调用时在 Local 作用域被运行时 `Set` 覆盖（命令返回时 Local 帧弹出，全局值恢复可见）。Per ADR-0049 §2.

| 变量 | 类型 | 默认值 | 含义 |
|---|---|---|---|
| `$WhatIfPreference` | bool | `$false` | 全局 WhatIf 开关（`[CmdletBinding(SupportsShouldProcess)]` 函数作用域可被 `-WhatIf` 覆盖） |
| `$ConfirmPreference` | enum | `High` | 确认阈值（`High` / `Medium` / `Low` / `None`，`-Confirm` 拉到 `Low`） |
| `$VerbosePreference` | enum | `SilentlyContinue` | `Write-Verbose` 输出阈值 |
| `$DebugPreference` | enum | `SilentlyContinue` | `Write-Debug` 输出阈值 |
| `$ErrorActionPreference` | enum | `Continue` | 错误处理策略（详见 §3.1，列在此处便于对照） |
| `$PSCmdlet` | CmdletContext | `$null` | cmdlet 上下文（`ShouldProcess` / `ShouldContinue` / `WriteVerbose` / `WriteWarning` / `WriteError` / `WriteObject`），仅在 `[CmdletBinding]` 函数 / 脚本块内由运行时 `Set` 注入（per ADR-0049 §8） |

> 注：`$PSCmdlet` 在普通函数内为 `$null`，仅 `[CmdletBinding]` 函数可见。`$ErrorActionPreference` 因历史原因也出现在 §3.1，本节为统一偏好变量类别而重列。

### 4. 类型化变量

修订后支持类型标注，运行时强制类型转换。

#### 4.1 类型标注语法

```powershell
[int]$count = 5
[string]$name = "hello"
[long]$big = 9223372036854775807
[double]$pi = 3.14159
[bool]$flag = $true
[DateTime]$when = "2026-07-08"

# 数组类型
[string[]]$names = @("a", "b", "c")
[int[]]$nums = 1, 2, 3
[object[]]$mixed = 1, "two", 3.0

# 哈希表
[hashtable]$config = @{ Key = "Value" }
[ordered]$orderedConfig = [ordered]@{ A = 1; B = 2 }   # 有序字典

# 自定义对象
[PSCustomObject]$obj = [PSCustomObject]@{ Name = "x"; Age = 30 }

# switch 参数（cmdlet 绑定用）
[switch]$Force
```

#### 4.2 类型强制规则

- 赋值时若值类型与标注不匹配，按 .NET 类型转换规则强制转换：
  - `string` → `int`：`int.Parse`（`"42"` → `42`）
  - `int` → `string`：`ToString()`
  - `string` → `bool`：`"true"` / `"1"` → `$true`，`"false"` / `"0"` / `""` → `$false`
  - `int` → `bool`：`0` → `$false`，非零 → `$true`
  - `string` → `DateTime`：`DateTime.Parse`
- 数组类型 `[T[]]`：对每个元素强制为 `T`，元素类型不匹配抛 `InvalidCastException`
- `[switch]` 类型默认值为 `$false`；cmdlet 绑定时 `-Force`（不带值）设为 `$true`
- 无类型标注时默认类型为 `object`（沿用初版行为）

#### 4.3 转换失败处理

类型转换失败抛 `InvalidCastException`（包装为 `ErrorRecord`，Category = `InvalidArgument`，退出码 4）。可被 `try { ... } catch { ... }` 捕获（参考 ADR-0045 Control Flow）。

```powershell
try {
    [int]$x = "abc"   # InvalidCastException
} catch {
    Write-Warning "转换失败: $_"
}
```

#### 4.4 类型持久性

- 类型标注**持久绑定到变量名**：`[int]$count` 后，后续 `$count = "42"` 仍会被强制为 `int`
- 重新声明可覆盖类型：`[string]$count = "x"` 改变 `$count` 的绑定类型
- `Remove-Variable count` 后重新创建为无类型 `object`

### 5. 变量成员访问

修订后支持完整的成员访问（初版被拒绝，现撤销）。

#### 5.1 语法

```powershell
$item.Name                       # 属性访问
$item.Properties["Size"]         # 索引属性
$item.Method()                   # 无参方法调用
$item.Method(arg1, arg2)         # 带参方法调用
$var.Count                       # 集合计数
$array.Length                    # 数组长度
$hash.Keys                       # 哈希表键集合
$hash.Values                     # 哈希表值集合
$string.ToUpper()                # 字符串方法
$var.ToUpper().Substring(0, 3)   # 方法链
```

#### 5.2 属性查找规则

- 通过 .NET 反射查找对象的公共属性（`Type.GetProperty`，**大小写不敏感**）
- 命中则返回属性值
- 未命中：
  - 若对象为 `IDictionary` / `hashtable` / `PSCustomObject`，尝试按键查找（`$hash.Name` 等价 `$hash["Name"]`）
  - 仍未命中抛 `PropertyNotFoundException`

#### 5.3 方法调用规则

- 通过反射查找公共实例方法（大小写不敏感）
- 参数按 .NET 类型转换规则强制
- 重载选择：按参数数量与类型兼容度选择最佳重载
- 返回值：`void` 方法返回 `$null`

#### 5.4 索引器

```powershell
$array[0]          # 第一个元素
$array[-1]         # 最后一个元素（负索引从末尾倒数）
$array[1..3]       # 切片（索引 1 到 3，闭区间）
$hash["key"]       # 哈希按键访问
$hash.key          # 等价 $hash["key"]（语法糖）
$string[0]         # 字符串首字符（返回 char）
$nested[0][1]      # 多维索引
```

#### 5.5 Null 条件访问（PowerShell 7+ 兼容）

```powershell
$var?.Name         # 若 $var 为 $null 返回 $null，否则返回 $var.Name
$var?.Method()     # 若 $var 为 $null 不调用方法
$var?[0]           # 若 $var 为 $null 返回 $null
```

### 6. 子表达式

修订后支持 `$(...)` 与 `@(...)` 子表达式（初版被拒绝，现撤销）。

#### 6.1 `$(...)` 子表达式

```powershell
$result = $($x + 1) * 2
"Value: $($var.Name.ToUpper())"
"Count: $(($items | Measure-Object).Count)"
$now = $(Get-Date)
```

语义：
- `$(...)` 求值括号内的一条或多条语句
- 返回**最后一条语句的输出**（若有多个输出则聚合成数组）
- 可嵌套：`$($( $a + $b ))`

#### 6.2 `@(...)` 数组子表达式

```powershell
$arr = @(Get-Process)         # 始终返回数组（即使只有一个进程）
$single = @("only one")       # 返回单元素数组
$empty = @()                  # 空数组
```

语义：
- `@(...)` 始终返回 `object[]`，即使内部只有一个输出或无输出
- 区别于 `$(...)`：`$(Get-Process)` 单结果时返回标量，`@(Get-Process)` 始终返回数组

#### 6.3 字符串内插值

双引号字符串中嵌入 `$(...)`：

```powershell
"Result: $($x + $y)"
"Name: $($user.Name), Age: $($user.Age)"
"Today: $(Get-Date -Format 'yyyy-MM-dd')"
```

`$(...)` 在双引号内求值后 `ToString()` 拼入字符串。

### 7. 哈希表与数组

修订后支持哈希表与数组字面量（初版被拒绝，现撤销）。

#### 7.1 哈希表字面量

```powershell
$hash = @{ Name = "John"; Age = 30; Active = $true }
$nested = @{ Outer = @{ Inner = "value" } }
$empty = @{}
```

语义：
- `@{ }` 创建 `[hashtable]`（`System.Collections.Hashtable`，**大小写不敏感键**，与 PowerShell 一致）
- 键值对以 `;` 或换行分隔
- 键可为标识符（`Name`）或字符串（`"Key Name"`）
- 值可为任意表达式（含子表达式、嵌套哈希）
- 有序哈希：`[ordered]@{ ... }` 创建 `OrderedDictionary`（保持插入顺序）

#### 7.2 数组字面量

```powershell
$arr = @(1, 2, 3, 4)
$mixed = 1, "two", 3.0        # 逗号分隔即数组（无需 @()）
$single = @("only")          # 单元素数组
$empty = @()                 # 空数组
$range = 1..5                # 范围数组：1, 2, 3, 4, 5
```

语义：
- `@( )` 创建 `[object[]]`，元素类型为 `object`
- 顶层逗号分隔的表达式也创建数组（`$a, $b, $c`）
- `,` 在 `@()` 内或顶层创建数组元素
- `1..5` 范围运算符生成 `[int[]]`，支持递减 `5..1` 与负数 `-3..-1`

#### 7.3 数组不可变语义

PowerShell 数组为定长，`+=` 创建新数组：

```powershell
$arr = @(1, 2, 3)
$arr += 4        # 实际创建新数组 (1, 2, 3, 4)，旧数组被 GC
```

- 频繁 `+=` 性能差（O(n) 每次复制），推荐用 `[System.Collections.ArrayList]` 或 `[List[T]]`
- 修改元素可直接索引赋值：`$arr[0] = 99`

#### 7.4 哈希访问与枚举

```powershell
$hash.Name            # 语法糖，等价 $hash["Name"]
$hash["Name"]         # 显式键访问
$hash.Keys            # 所有键（大小写不敏感哈希返回原大小写键名）
$hash.Values          # 所有值
$hash.Count           # 键值对数量
$hash.ContainsKey("Name")   # bool
$hash.ContainsValue(30)      # bool
```

- 哈希枚举：`foreach ($k in $hash.Keys) { $hash[$k] }`
- 哈希为引用类型，函数内修改影响外部（除非显式 `.Clone()`）

### 8. Splatting

修订后支持 splatting（初版被拒绝，现撤销）。

#### 8.1 哈希 splatting（命名参数）

```powershell
$params = @{
    Path        = "C:\temp"
    Force       = $true
    Recurse     = $true
    Filter      = "*.txt"
}
Copy-Item @params
# 等价于 Copy-Item -Path "C:\temp" -Force -Recurse -Filter "*.txt"
```

- `@Variable`（注意是 `@` 不是 `$`）将哈希表展开为命名参数
- 哈希键与 cmdlet 参数名匹配（大小写不敏感）
- 哈希中多余的键（cmdlet 无对应参数）抛 `ParameterBindingException`，除非 cmdlet 接受 `*` 通配

#### 8.2 数组 splatting（位置参数）

```powershell
$args = @("source.txt", "dest.txt")
Move-Item @args
# 等价于 Move-Item "source.txt" "dest.txt"
```

- `@Variable` 将数组展开为位置参数
- 按数组顺序绑定到 cmdlet 的位置参数槽

#### 8.3 `$PSBoundParameters` 转发

```powershell
function Invoke-WithLog {
    param([string]$Path, [switch]$Force)
    # 转发所有已绑定参数到 Copy-Item
    $params = @{ Path = $Path } + $PSBoundParameters
    Copy-Item @params
}
```

- `@PSBoundParameters` 是最常见 splatting 用法：转发当前函数绑定的所有参数
- 常用于包装函数（proxy function）

#### 8.4 splatting 与显式参数混用

```powershell
Copy-Item @params -Destination "C:\backup"
```

- 显式参数优先级高于 splatting（显式参数覆盖 splatting 中的同名键）

### 9. Here-strings

修订后支持 here-string（初版被拒绝，现撤销）。

#### 9.1 可插值 here-string

```powershell
$multi = @"
Line 1
Line 2 with $variable interpolation
Line 3 with $($expr) sub-expression
"@
```

- `@"` 必须是该行最后一个字符（之后不能有其他内容）
- `"@` 必须在闭合行的**行首**（前导不能有空格）
- 内部支持 `$var` / `${var}` / `$(...)` 插值（与双引号字符串规则一致）

#### 9.2 字面 here-string

```powershell
$literal = @'
No interpolation
$variable stays literal
$(expr) stays literal
'@
```

- `@'` 必须是该行最后一个字符
- `'@` 必须在闭合行的行首
- 内部**不**做任何插值（与单引号字符串规则一致）

#### 9.3 嵌套与多行

here-string保留所有换行与缩进（包括开闭标记之间的空行）。多行文本常用于：

- 生成 JSON / XML / YAML
- 多行 SQL 查询
- 模板字符串

### 10. 环境变量

#### 10.1 读写

```powershell
$env:PATH                          # 读：Environment.GetEnvironmentVariable("PATH")
$env:MY_VAR = "value"              # 写：Environment.SetEnvironmentVariable("MY_VAR", "value")
$env:PATH += ";C:\tools"           # 追加
```

#### 10.2 列举与删除

```powershell
Get-ChildItem env:                 # 枚举所有环境变量（需 Env Provider，见 §10.3）
Get-ChildItem env:PATH            # 枚举名为 PATH 的环境变量
Remove-Item env:MY_VAR             # 删除：Environment.SetEnvironmentVariable("MY_VAR", null)
```

#### 10.3 Env Provider

- `Get-ChildItem env:` / `Remove-Item env:NAME` 需要 Env Provider（基于 ADR-0001 Provider 系统）
- Env Provider 在 M3 实现（修订后从 M4 提前，因环境变量列举为 shell 基础体验）
- 在 Env Provider 就绪前，`$env:NAME` 读写通过 OS 桥接直接支持，列举暂用 `[Environment]::GetEnvironmentVariables()`

#### 10.4 跨平台大小写

- Windows：环境变量名大小写不敏感
- Linux / macOS：环境变量名大小写敏感
- OpenShell 在所有平台上尊重 OS 语义（`$env:path` 与 `$env:PATH` 在 Windows 上等价，在 Linux 上不同）

### 11. 持久化

修订后新增显式持久化命令（初版仅定义 `export-variable` 语义且推迟到 M5，修订后提前到 M3）。

#### 11.1 命令

```powershell
Export-Variable -Name myEditor           # 持久化单个变量到 ~/.openshell/variables.json
Export-Variable -Name myEditor -Scope session
Import-Variable -Name myEditor           # 从文件加载
```

#### 11.2 存储格式

修订后从 TOML 改为 JSON（因需表达类型信息、嵌套哈希、数组）：

```json
{
  "variables": [
    {
      "name": "myEditor",
      "value": "vim",
      "type": "string",
      "scope": "session"
    },
    {
      "name": "config",
      "value": { "Key": "Value" },
      "type": "hashtable",
      "scope": "script"
    }
  ]
}
```

存储路径：`~/.openshell/variables.json`

#### 11.3 自动导入

启动时若 `~/.openshell/variables.json` 存在，自动 `Import-Variable` 所有持久化变量到 `global:` 作用域（参考 ADR-0041 Startup Script 的加载顺序）。

#### 11.4 限制

- 不可持久化 `private:` 变量（作用域退出即销毁）
- 不可持久化 `using:` 变量（远程作用域专属）
- 不可持久化自动变量（系统提供，运行时计算）
- 不可持久化闭包捕获的变量（参考 ADR-0046 Script Blocks）
- 值为非可序列化对象（如 `FileStream`、`Process`）时抛 `SerializationException`

### 12. 接口契约

修订后接口扩展以支撑类型化、作用域栈、成员访问。

```csharp
public interface IVariableRegistry
{
    // 基础读写
    object? Resolve(string name, VariableScope? scope = null);
    void Set(string name, object value, VariableScope scope = VariableScope.Local);
    bool Remove(string name, VariableScope scope = VariableScope.Local);

    // 类型化变量
    void SetTyped(string name, object value, Type type, VariableScope scope = VariableScope.Local);
    Type? GetVariableType(string name, VariableScope? scope = null);

    // 列举与查询
    IReadOnlyList<VariableEntry> List(VariableScope? scope = null);
    bool IsReadOnly(string name);                              // 自动变量返回 true
    bool IsPrivate(string name);                               // private: 变量返回 true
    VariableScope? GetScope(string name);                     // 返回变量所在作用域

    // 自动变量（核心系统专用）
    void SetAutomatic(string name, object value);

    // 作用域栈
    IDisposable PushScope();                                  // 压入新 local 栈帧，Dispose 弹出
    IVariableRegistry GetAncestor(int n);                     // 第 N 个祖先作用域
}

public sealed class VariableEntry
{
    public string Name { get; init; }
    public object? Value { get; init; }
    public Type? DeclaredType { get; init; }     // null = 无类型标注
    public VariableScope Scope { get; init; }
    public bool IsPrivate { get; init; }
}

public enum VariableScope
{
    Global,    // 进程级
    Script,    // 脚本/模块级
    Local,     // 当前栈帧（默认）
    Private,   // 私有（不向子作用域继承）
    Using,     // 远程/并行传参
}

public static class VariableExpander
{
    public static string Expand(string input, IVariableRegistry vars);
    public static bool TryResolve(string token, IVariableRegistry vars, out object? value);

    // 修订新增：成员访问
    public static object? GetMember(object obj, string member, object?[]? index = null);
    public static object? InvokeMethod(object obj, string method, object?[] args);

    // 修订新增：子表达式求值
    public static object? EvaluateSubExpression(string expr, IVariableRegistry vars);
    public static object[] EvaluateArraySubExpression(string expr, IVariableRegistry vars);
}
```

### 13. 求值流程

替换 M1 CliHost 硬编码 if：

```
line = "$LASTEXITCODE"
↓
VariableExpander.TryResolve(line, out var value)
↓ 命中
Console.WriteLine(value)
continue;
```

修订后求值流程扩展支持成员访问与子表达式：

```
"count is $($count + 1)"
↓
VariableExpander.Expand
  → 遇到 $(...)
  → EvaluateSubExpression(" $count + 1 ")
    → 解析表达式 → 解析 $count → Resolve("count") → 算术 + 1
  → ToString 拼入
```

成员访问求值：

```
$item.Name
↓
VariableExpander.TryResolve
  → 解析 $item → Resolve("item")
  → 解析 .Name → GetMember(value, "Name")
```

### 14. 插值规则

#### 14.1 双引号字符串

```powershell
"hello $name"                 # → "hello world"
"path: ${PWD}"                # → "path: fs::C:/Users"
"count: $($count + 1)"        # 子表达式求值
"item: $item.Name"            # → "item: <Name 属性值>"（成员访问插值）
"hash: $($hash.Key)"          # → "hash: <值>"
"arr: $($arr[0])"             # → "arr: <首元素>"
```

#### 14.2 单引号字符串

```powershell
'hello $name'                 # → "hello $name"（字面量，不插值）
```

#### 14.3 here-string

见 §9。

#### 14.4 转义

- 双引号内 `` `$ `` 转义为字面 `$`
- 双引号内 `` `" `` 转义为字面 `"`
- 单引号内 `''` 转义为字面 `'`

### 15. 类型转换

变量值存储时保留声明类型（若有标注）；使用时按上下文转换：

```powershell
where size > $threshold        # $threshold 转 long
copy-item $src $dest           # 转 ItemPath
if ($count -gt 0) { ... }      # 转 int
foreach ($x in $collection) { ... }   # 集合枚举
```

- 已声明类型的变量：赋值时已强制，读取时无需再转
- 未声明类型（`object`）：按上下文转换，失败抛 `InvalidCastException`

### 16. 错误处理

参考 ADR-0026，变量相关错误统一退出码 4（`ErrorCategory.InvalidArgument`）：

| 场景 | 异常 | 退出码 |
|---|---|---|
| 未定义变量 `$undefined` | `VariableNotFoundException` | 4 |
| 自动变量赋值 `$? = 0` | `ReadOnlyVariableException` | 4 |
| 类型转换失败 | `InvalidCastException`（包装为 ErrorRecord） | 4 |
| 属性未找到 `$obj.NoSuchProp` | `PropertyNotFoundException` | 4 |
| 方法未找到 `$obj.NoSuchMethod()` | `MethodInvocationException` | 4 |
| 索引越界 `$arr[100]`（数组仅 3 元素） | `IndexOutOfRangeException`（包装为 ErrorRecord） | 4 |
| `using:` 赋值 `$using:x = 1` | `InvalidOperationException` | 4 |
| 持久化不可序列化对象 | `SerializationException` | 4 |
| splatting 参数不匹配 | `ParameterBindingException` | 4 |

异常类继承 `OpenShellException`，统一通过 `IErrorStream` 输出。`$Error[0]` 自动更新为最近错误。

### 17. CliHost 集成示例

```csharp
// 替换 M1-8 的硬编码 if (line is "$?") ... if (line is "$LASTEXITCODE") ...
if (VariableExpander.TryResolve(line.Trim(), _vars, out var value))
{
    await WriteOutputLineAsync(value?.ToString() ?? "");
    continue;
}

// 在 StripGlobalSwitches 后做插值
line = VariableExpander.Expand(line, _vars);

// 函数调用时压入新作用域
using (_vars.PushScope())
{
    // 函数体内 $args / $input / $PSBoundParameters 可见
    await ExecuteBlock(functionBody);
}
// 作用域自动弹出，local 变量清理
```

### 18. 与相关 ADR 联动

- **ADR-0024（别名与函数）**：函数体作用域由本 ADR 的作用域栈支撑，`$args` / `$input` / `$PSBoundParameters` 在函数调用时自动注入
- **ADR-0045（Control Flow）**：`try/catch` 捕获变量类型转换异常；`$switch` / `$foreach` 循环枚举器变量
- **ADR-0046（Script Blocks）**：脚本块作为一等公民捕获外层作用域变量（闭包语义），`$_` / `$PSItem` 在 `process {}` 块内绑定
- **ADR-0048（Cmdlets）**：`$PSCmdlet` 提供 cmdlet 上下文；`[switch]` 类型标注支撑 cmdlet 开关参数
- **ADR-0049（ShouldProcess）**：`$PSCmdlet.ShouldProcess()` / `ShouldContinue()` 通过 `$PSCmdlet` 变量暴露
- **ADR-0010（Pipeline Object Stream）**：管道对象通过 `$_` 流转，成员访问支持对象属性遍历
- **ADR-0041（Startup Script）**：`$PROFILE` 多字段对象与启动脚本加载顺序联动；自动导入 `variables.json`

## Removed Restrictions（移除的限制）

修订撤销初版 ADR-0042 的以下限制（现全部支持）：

| 初版限制 | 修订后状态 |
|---|---|
| ❌ 初版：不支持 `$var.Property` 成员访问 | ✅ 修订：支持（§5） |
| ❌ 初版：不支持 `$(...)` 子表达式 | ✅ 修订：支持（§6） |
| ❌ 初版：不支持 `@{}` 哈希字面量 | ✅ 修订：支持（§7） |
| ❌ 初版：不支持 `@()` 数组字面量 | ✅ 修订：支持（§7） |
| ❌ 初版：不支持类型化变量 `[int]$x` | ✅ 修订：支持（§4） |
| ❌ 初版：不支持 splatting `@params` | ✅ 修订：支持（§8） |
| ❌ 初版：不支持 `private:` 作用域 | ✅ 修订：支持（§2） |
| ❌ 初版：不支持 `using:` 作用域 | ✅ 修订：支持（§2.4） |
| ❌ 初版：不支持 here-string `@"..."@` | ✅ 修订：支持（§9） |
| ❌ 初版：3 层静态作用域（Global/Script/Session） | ✅ 修订：5 层栈式作用域（§2） |
| ❌ 初版：值统一 `object` 无类型标注 | ✅ 修订：支持类型标注与运行时强制（§4） |
| ❌ 初版：`$Host` 为字符串 | ✅ 修订：`$Host` 为完整宿主对象（§3.2） |

## Alternatives Considered

1. **保持硬编码特例**：被否决，新增每个变量都要改 CliHost，无法支撑 ADR-0024 函数体内的 `$args` / `$input`，扩展性差
2. **不支持用户变量**：被否决，函数体无法参数化（`$path` / `$sizeMB` 无法赋值），`$x = ...` 是基础 shell 体验
3. **`$env:` 直接走 Env Provider（M4 提前）**：被否决（初版立场），修订后部分采纳——Env Provider 提前到 M3（§10.3），但 `$env:NAME` 读写仍走 OS 桥接，列举才需 Env Provider
4. **简化 3 层作用域 + 无类型 object（初版 ADR-0042 设计）**：被否决（**2026-07-08 修订动因**），因阻碍 PowerShell 兼容性——缺少 `private:` / `using:` 无法表达私有与远程变量；无类型标注无法支撑 `[int]$count` 等强制语义；拒绝成员访问/子表达式/字面量/splatting 阻塞 `.ps1` 脚本移植。修订后采用完整 5 层作用域栈 + 类型化变量 + 全部字面量/成员/子表达式/splatting 支持
5. **完全实现 PowerShell 变量作用域栈含所有边角语义**：被否决，如 `Get-Variable -Scope` 的负数索引、`Set-Variable -Option AllScope`、`$PSDefaultParameterValues` 等高级语义在 M3 阶段不实现，留到 M4+ 按需添加
6. **变量值强类型化（强制每个变量声明类型）**：被否决，强制类型破坏 shell 灵活性；修订采用「可选类型标注 + 默认 object」（§4），既支持类型强制又保留 shell 弱类型体验
7. **使用 TOML 持久化**：被否决，TOML 不擅长表达嵌套哈希与数组；修订改用 JSON（§11.2）以支撑类型化持久化
8. **直接复用 PowerShell 引擎（System.Management.Automation）**：被否决，OpenShell 需保持跨平台独立实现与自有对象模型（ADR-0003 Immutable Item Model）；采用「语义对齐 + 自研实现」策略

## Consequences

### 优势

- **完整 PowerShell 变量兼容**：`.ps1` 脚本可直接移植，类型标注 / 成员访问 / 子表达式 / 哈希数组 / splatting / here-string 全部支持
- **统一求值入口**：CliHost 只调用 `VariableExpander.TryResolve` / `Expand`，不再硬编码 if
- **可扩展**：新增自动变量只需在 `IVariableRegistry` 实现里注册，不动 ReplEngine
- **与 ADR-0024 函数机制联动**：函数体内 `$args` / `$input` / `$PSBoundParameters` / `$MyInvocation` 通过统一注册表求值
- **与 ADR-0046 Script Blocks 闭包语义联动**：作用域栈支撑脚本块变量捕获
- **与 ADR-0048/0049 Cmdlet 上下文联动**：`$PSCmdlet` 暴露 `ShouldProcess` / `WriteVerbose` 等
- **环境变量桥接**：`$env:PATH` 语法对 shell 用户友好，无需命令包装
- **类型安全可选**：类型标注提供运行时类型检查，未标注时保留 shell 弱类型灵活性
- **作用域语义完整**：`private:` 支持封装，`using:` 支持远程/并行传参，覆盖 PowerShell 核心场景

### 代价

- **作用域栈性能开销**：每次函数/脚本块调用压入新栈帧，约 **1μs/次栈帧分配**（基于 `Stack<Dictionary>` 估算）；高频调用场景需关注
- **类型化变量运行时开销**：每次赋值需类型检查与强制转换，约 0.1μs/次（反射 `Convert.ChangeType`）；类型化变量密集场景需关注
- **成员访问反射开销**：`$var.Property` 通过反射查找，首次约 5μs；可缓存 PropertyInfo 优化
- **接口扩展**：`IVariableRegistry` 新增 `SetTyped` / `GetVariableType` / `IsPrivate` / `PushScope` / `GetAncestor` 等方法，M2 实现需迁移到 M3 扩展
- **`InMemoryVariableRegistry` 重构**：初版的 3 个并发字典（`_global` / `_script` / `_session`）需重构为作用域栈结构，`Session` 顶层并入 `global:`
- **变量作用域语义需文档化**：`private:` 不向子作用域继承 / `using:` 仅远程有效 / 栈帧查找规则需用户文档说明
- **类型系统边界 case**：`object` + 运行时转换可能在复杂场景失败（如 `$count` 为 `string` 但被当 `int` 用），需错误信息友好
- **`$env:` 桥接与 OS 同步**：跨平台环境变量大小写规则（Windows 不区分、Linux 区分）需在文档说明
- **插值性能**：双引号字符串每次都要扫描 `$` 与 `$(...)`，对长字符串有轻微开销
- **持久化迁移**：从 TOML 改为 JSON，初版已实现 TOML 序列化（若有）需迁移

### 约束

- 自动变量永远只读（核心系统经 `SetAutomatic` 写入），赋值抛 `ReadOnlyVariableException`
- 变量名禁止含 `-`（避免与 Verb-Noun 混淆）、禁止以数字开头
- 变量名仅允许 `[A-Za-z_][A-Za-z0-9_]*`（`$?` 为唯一特例）
- 作用域修饰符前缀（`global:` / `script:` / `local:` / `private:` / `using:` / `env:`）大小写不敏感
- `using:` 变量仅在远程/并行作用域内可读，赋值抛 `InvalidOperationException`
- `private:` 变量不向子作用域继承，子作用域内 `$private:Var` 引用父作用域 private 变量抛 `VariableNotFoundException`
- 类型转换失败统一退出码 4，`ErrorCategory` 为 `InvalidArgument`
- 自动变量值在每次读取时按需计算（如 `$PWD` 读 `CurrentLocation`），不缓存到作用域
- `VariableExpander.Expand` 仅在双引号字符串 / 可插值 here-string 内生效，单引号与字面 here-string 不变
- 自动变量新增必须更新本 ADR 的清单表（§3），禁止在代码里隐式添加
- 持久化仅支持可序列化值（基础类型 / 哈希 / 数组 / PSCustomObject），不可序列化对象抛 `SerializationException`
- 数组为定长，`+=` 创建新数组（不可变语义）
- 哈希表默认大小写不敏感键（`Hashtable`），`[ordered]@{ }` 创建有序字典
- here-string 闭合标记（`"@` / `'@`）必须在行首
- splatting `@Variable` 中 `Variable` 必须为 `hashtable` 或 `array`，否则抛 `InvalidOperationException`
- 子表达式 `$(...)` 内可执行任意语句，`@(...)` 始终返回数组

## Migration（从初版到修订）

### 代码迁移

- `VariableScope` 枚举：`{ Global, Script, Session }` → `{ Global, Script, Local, Private, Using }`，`Session` 映射到 `Local`（默认作用域）
- `IVariableRegistry`：新增 `SetTyped` / `GetVariableType` / `IsPrivate` / `GetScope` / `PushScope` / `GetAncestor`
- `InMemoryVariableRegistry`：3 个并发字典重构为作用域栈（`Stack<ScopeFrame>`，每帧含 `Dictionary<string, VariableEntry>`）
- `VariableExpander`：新增 `GetMember` / `InvokeMethod` / `EvaluateSubExpression` / `EvaluateArraySubExpression`
- `$Host` 类型从 `string` 改为 `HostObject`（含 `.UI` / `.Version` / `.Name`）

### 兼容性

- 现有脚本中 `$Host` 为字符串比较的代码（如 `if ($Host -eq "Cli")`）需改为 `if ($Host.Name -eq "Cli")`
- 现有 `export-variable` TOML 文件需迁移为 JSON（提供 `migrate-variables` 工具）
- `$ARGS` / `$INPUT` / `$ERROR` / `$ERRORS` 大写别名保留，大小写不敏感

## References

- PowerShell about_Variables: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_variables
- PowerShell about_Scopes: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_scopes
- PowerShell about_Automatic_Variables: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_automatic_variables
- PowerShell about_Hash_Tables: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_hash_tables
- PowerShell about_Arrays: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_arrays
- PowerShell about_Splatting: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_splatting
- PowerShell about_Quoting_Rules: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_quoting_rules
- PowerShell about_Type_Literals: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_type_literals
- ADR-0001 Provider Capability Composition
- ADR-0008 CLI REPL Architecture
- ADR-0010 Pipeline Object Stream
- ADR-0024 Aliases and Functions
- ADR-0026 Error Model & Exit Codes
- ADR-0041 Startup Script
- ADR-0045 Control Flow
- ADR-0046 Script Blocks
- ADR-0048 Cmdlets
- ADR-0049 ShouldProcess
