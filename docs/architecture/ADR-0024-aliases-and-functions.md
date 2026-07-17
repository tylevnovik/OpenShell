# ADR-0024: 别名与用户自定义函数

- **Status**: Accepted
  - Revised 2026-07-08: switched from TOML-only function definitions to PowerShell-style `function` syntax with `param()` and begin/process/end blocks. TOML format preserved for declarative aliases only.
- **Date**: 2026-07-07 (revised 2026-07-08)
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0004 (命令系统), ADR-0008 (CLI REPL), ADR-0022 (配置), ADR-0023 (命令清单), ADR-0041 (Startup Script), ADR-0042 (自动变量), ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0047 (Variable System), ADR-0048 (Cmdlets)

## Context

ADR-0004 的 `[Verb(Aliases = new[]{"ls"})]` 仅支持内置别名（编译时固定）。实际使用还需要：

1. **用户自定义别名**：`alias ll="get-childitem -l"`、`alias gp="get-itemproperty"`
2. **用户函数**：多语句组合，带命名参数、控制流、`begin/process/end` 管道处理段
3. **别名与命令冲突**：用户定义 `ls` 覆盖内置，如何解决
4. **别名持久化**：跨会话保留
5. **别名与管道**：`ll | where size > 1MB` 能否工作
6. **作用域**：全局别名 vs 项目级别名（`.openshell/aliases.toml`）
7. **别名管理命令**：`set-alias` / `get-alias` / `remove-alias` / `export-alias`

最初（2026-07-07）本 ADR 选择"TOML-only 函数"路线：函数体在 `functions.toml` 中以 `[[function]]` 表声明，仅允许命令 + 管道，明确拒绝控制流、变量赋值、嵌套定义与 `begin/process/end`。理由是"避免变成脚本语言"，复杂逻辑让用户写 C# 插件。

用户在 2026-07-08 决定走 "PowerShell 全兼容" 路线（与 ADR-0012 修订一致）。原 TOML-only 设计阻断 PowerShell 现有 `function` 脚本的迁移，也无法支撑 `ForEach-Object` / `Where-Object` 等需要的脚本块语义。因此本 ADR 修订为：**以 PowerShell 风格 `function` 关键字 + `param()` + `begin/process/end` 为主机制**，原 TOML `[[function]]` 格式降级为向后兼容的语法糖，加载时 lowering 为单段 `process` 块的 `function`。TOML `[[alias]]` 别名声明保持不变。

修订后的预期用法：

```powershell
# 别名（TOML 声明，文本替换，未变）
[[alias]]
name = "ll"
command = "get-childitem -l"

# 函数（PowerShell 风格，主机制）
function Copy-Backup {
    param(
        [string]$Source,
        [string]$Destination,
        [switch]$Force
    )
    begin {
        $stamp = Get-Date -Format "yyyyMMdd"
    }
    process {
        Copy-Item -Path $Source -Destination "$Destination.$stamp" -Force:$Force
    }
    end {
        Write-Output "Backup complete"
    }
}
```

参考：

- PowerShell `function` / `param()` / `begin/process/end` / `CmdletBinding` 模型
- ADR-0012 修订（PowerShell 全兼容路线已确立）
- ADR-0042 自动变量（`$args` / `$input` / `$PSBoundParameters`）
- ADR-0046 脚本块引擎（函数体与脚本块共享求值路径）

## Decision

### 1. 三层别名机制

| 层级 | 来源 | 优先级 | 持久化 |
|---|---|---|---|
| 内置 | `[Verb(Aliases)]` 特性 | 低 | 编译时 |
| 用户全局 | `~/.openshell/aliases.toml` | 中 | 配置文件 |
| 会话临时 | `set-alias` 命令 | 高 | 进程生命周期 |

同名时高优先级覆盖低优先级，`get-alias <name>` 显示当前生效的来源。函数优先级高于别名（见 §9）。

### 2. 别名定义格式（未变）

`~/.openshell/aliases.toml`：

```toml
[[alias]]
name = "ll"
command = "get-childitem -l"
description = "long listing"

[[alias]]
name = "gp"
command = "get-itemproperty"
description = "get properties"

[[alias]]
name = "touch"
command = "new-item -type file"
```

别名展开是**文本替换**（不是 AST 替换），展开后重新解析：

- `ll -r` → `get-childitem -l -r`
- `gp fs::HKLM/...` → `get-itemproperty fs::HKLM/...`
- 别名内可含管道：`alias ll="get-childitem | sort by name"`

TOML 仅保留 `[[alias]]` 声明式别名。`[[function]]` 表式函数已废弃（见 §3）。

### 3. 函数：PowerShell 风格 function（主机制）

**主要机制** —— PowerShell `function` 关键字 + `param()` 块 + 可选 `begin/process/end` 段：

```powershell
function Copy-Backup {
    param(
        [string]$Source,
        [string]$Destination,
        [switch]$Force
    )
    begin {
        $stamp = Get-Date -Format "yyyyMMdd"
    }
    process {
        Copy-Item -Path $Source -Destination "$Destination.$stamp" -Force:$Force
    }
    end {
        Write-Output "Backup complete"
    }
}
```

函数可在以下位置定义并进入会话函数表：

- REPL 输入（即时定义）
- `profile.openshell` 启动脚本（见 ADR-0041，定义后在后续 REPL 输入中可用）
- `.ops1` 脚本文件（经 dot-source `. file.ops1` 加载）
- `.openshell/functions.ops1` 项目级 / 用户级函数脚本

**别名** —— 仍由 TOML `[[alias]]` 表声明（§2），用于简单命令重命名，未变。

**TOML 函数（已废弃，向后兼容）** —— 旧 `[[function]]` 格式保留但标记废弃，加载时 lowering 为"仅含单段 `process` 块"的 `function`：

```toml
# ~/.openshell/functions.toml —— 已废弃，保留向后兼容
[[function]]
name = "find-large"
parameters = ["path", "sizeMB"]
body = """
get-childitem -r $path | where size > ($sizeMB * 1MB) | sort by size desc | format-table name,size
"""
description = "Find files larger than N MB"
```

等价 lowering 为：

```powershell
function find-large {
    param($path, $sizeMB)
    process {
        get-childitem -r $path | where size > ($sizeMB * 1MB) | sort by size desc | format-table name,size
    }
}
```

`parameters` 列表中的每个名字变成无类型注解的 `param()` 形参；`body` 整体进入 `process` 块。lowering 后与原生 `function` 共享同一求值路径，无运行时差异。`Get-Function` 输出对 lowering 产生的函数标记 `Source = toml-deprecated`，首次调用时打印一次废弃警告。

### 4. 函数语法

Grammar（方括号 `[]` 表示可选段）：

```
function <name> {
    [param([type]$name = default, ...)]
    [begin   { <statements> }]
    [process { <statements> }]
    [end     { <statements> }]
    [<statements>]
}
```

规则：

- `param()` 块若存在，必须是函数体内**第一个**语句（注释除外）。
- 单块体（无 `begin/process/end`）视为 `process` 块，与 PowerShell 一致：
  ```powershell
  function Greet { param([string]$Name) "Hello, $Name" }
  # 等价于
  function Greet { param([string]$Name); process { "Hello, $Name" } }
  ```
- `begin/process/end` 三段按出现顺序无关：执行时固定按 `begin → process(每输入一次) → end`。
- `process` 块在管道模式下对每个上游输入项调用一次；无管道输入时 `process` 调用一次（`$input` 为空枚举）。
- `begin` / `end` 整个函数调用周期内各调用一次。
- 函数体内允许任意控制流：`if` / `elseif` / `else` / `while` / `for` / `foreach` / `switch` / `try-catch-finally`（详见 ADR-0045）。
- `return [<expr>]` 退出当前函数并返回值；省略 expr 等价 `return $null`。
- `break` / `continue` 用于循环控制；在 `process` 块内的 `break` 跳出当前循环，不退出函数。
- 嵌套函数定义允许：函数体内可定义局部函数，作用域为当前函数（外层不可见）。
- 管道输入经 `$input` 自动变量（`IAsyncEnumerable<IItem>`）或 `process` 块内的 `$_` / `$PSItem`（当前输入项）访问。
- 函数名遵循 Verb-Noun 规范（ADR-0023）；不允许与内置命令同名时静默覆盖，须警告（见 §9）。

### 5. 参数绑定

参数声明语法：

```powershell
function Do-Thing {
    param(
        [Parameter(Mandatory, Position=0)]
        [string]$Source,

        [Parameter(Position=1)]
        [string]$Destination = "./out",

        [Parameter(ValueFromPipeline)]
        [string]$InputItem,

        [Parameter(ValueFromPipelineByPropertyName)]
        [string]$Name,

        [switch]$Force,
        [int]$Count = 1,
        [string[]]$Tags = @()
    )
    process { ... }
}
```

**类型注解**：`[string]` / `[int]` / `[long]` / `[bool]` / `[string[]]` / `[switch]` / 自定义类型。运行时按类型转换，失败抛 `InvalidArgument`（ADR-0026）。

**属性（attribute）**：

| 属性 | 含义 |
|---|---|
| `[Parameter(Mandatory)]` | 必填，缺失时交互式提示或报错 |
| `[Parameter(Position=N)]` | 位置绑定，从 0 开始左到右 |
| `[Parameter(ValueFromPipeline)]` | 绑定上游每个输入项整体 |
| `[Parameter(ValueFromPipelineByPropertyName)]` | 按输入项属性名绑定 |
| `[switch]` | 布尔开关，默认 `$false`，带 `-Force` 即 `$true` |

**默认值**：`= "default"` 形式；未提供且非 Mandatory 时使用默认值。

**绑定方式**：

- **位置绑定**：`Do-Thing "src" "dst"` → `$Source="src"`, `$Destination="dst"`
- **命名绑定**：`Do-Thing -Source "src" -Destination "dst"`，参数名大小写不敏感
- **混合**：`Do-Thing "src" -Destination "dst"`，位置参数填充剩余未命名槽
- **switch**：`Do-Thing -Force` 设 `$Force=$true`；`-Force:$false` 显式置假
- **Splatting**：`@PSBoundParameters` 把已绑定参数展开为另一命令的命名参数：
  ```powershell
  function Wrap-Copy {
      param([string]$Source, [string]$Destination, [switch]$Force)
      process {
          Copy-Item @PSBoundParameters
      }
  }
  ```
- **数组参数**：`Do-Thing -Tags "a","b","c"` 或位置传递逗号分隔列表

**公共参数**：所有函数自动支持 `-Verbose` / `-Debug` / `-WhatIf` / `-Confirm` / `-ErrorAction` / `-WarningAction`（与 cmdlet 一致，见 ADR-0048）。函数体内可通过 `$VerbosePreference` 等自动变量读取偏好。

**管道绑定语义**：

- `[Parameter(ValueFromPipeline)]`：每个上游 `IItem` 整体赋给该参数，并触发一次 `process` 块执行
- `[Parameter(ValueFromPipelineByPropertyName)]`：从输入项的属性（`item.Properties["name"]`）取值赋参
- 多个管道输入 → `process` 块执行多次，`begin`/`end` 各一次

### 6. 作用域

- 函数在**新的局部作用域**内执行（per ADR-0042 revised）。函数内赋值 `$x = ...` 默认进入函数局部作用域，函数返回后销毁。
- 函数内可读取外层作用域变量（Session / Script / Global），写入默认进局部；用 `$script:var` / `$global:var` 显式写外层（ADR-0042 §10）。
- `$args`：自动数组，包含**未被命名绑定的位置参数**。已被命名参数消费的位置不再出现在 `$args` 中。
- `$PSBoundParameters`：hashtable，仅包含本次调用中**已成功绑定**的命名参数（不含位置未绑定项、不含默认值填充项）。
- `$input`：`IAsyncEnumerable<IItem>`，上游管道输入；仅当函数被管道调用时有效，否则为空枚举。
- `$_` / `$PSItem`：`process` 块内当前输入项（与 ADR-0012 一致）。
- 自动变量（`$PWD` / `$HOST` / `$LASTEXITCODE` 等）只读，函数内可读不可写（ADR-0042 §2）。
- 嵌套函数定义进入嵌套局部作用域，遵循词法作用域查找规则（从内向外）。

### 7. 模块集成

- **`.ops1` 脚本文件**：函数可定义在 `.ops1` 文件中，经 dot-source 加载：
  ```
  . ./mylib.ops1
  ```
  加载后函数进入当前会话函数表（Session 级）。
- **profile 内定义**：`profile.openshell` 中定义的函数在后续 REPL 输入中可用（ADR-0041 §5）。
- **`Export-ModuleMember`（未来）**：M3 模块系统将提供显式导出机制；当前阶段所有 dot-source 加载的函数默认进入会话作用域，导出语义留待 ADR-0048 + 模块 ADR 定义。
- **项目级函数**：`<project>/.openshell/functions.ops1` 在进入该目录时自动 dot-source 加载（与 `aliases.toml` 同层，见 §13）。
- **函数优先级**：会话临时（`set-function` / REPL / dot-source）> 项目级 > 用户全局 > 内置命令。

### 8. 函数 vs 别名的边界

| 特性 | 别名 | 函数 |
|---|---|---|
| 命名参数 | ❌ | ✅（含类型 / 默认值 / 属性） |
| 多语句 | ❌ | ✅ |
| 控制流 | ❌ | ✅ |
| `begin/process/end` | ❌ | ✅ |
| 管道输入 | 文本拼接 | ✅（`$input` / `$_` / `ValueFromPipeline`） |
| 递归 | ❌ | ✅ |
| 异常处理 | ❌ | ✅（`try-catch-finally`） |
| 性能 | 文本替换，无开销 | 参数解析 + 局部作用域，轻微开销 |
| 用途 | 短命令缩写 | 组合逻辑 / 管道段 / 业务封装 |

简单短缩写用别名，任何超出"纯文本替换"的需求用函数。

### 9. 冲突解决

```
用户调 "ll -r"
    ↓
查别名表（用户全局 > 内置）
    ↓ 命中别名
展开为 "get-childitem -l -r"
    ↓
重新解析 + 调度

用户调 "Copy-Backup -Source a -Destination b"
    ↓
查函数表（会话 > 项目 > 用户全局）
    ↓ 命中函数
绑定参数 + 新建局部作用域 + 执行 begin/process/end
```

冲突规则：

- 用户别名覆盖内置别名（`ls` 重新定义生效）
- 别名覆盖命令全名（`get-childitem` 可被别名覆盖，警告）
- **函数优先级最高**：函数覆盖同名别名与命令
- `get-alias ls` / `get-function Copy-Backup` 显示当前解析来源
- 启动时检测循环别名（`a → b → a`），报错；函数递归调用合法（如 `Get-Factorial` 调用自身），由调用深度上限（默认 100）防止栈溢出

### 10. 管道与函数的兼容

函数作为管道节点时：

```powershell
get-childitem | Copy-Backup -Destination "D:/bak"
```

- 上游 `IItem` 流逐项进入 `process` 块，`$_` 绑定当前项
- `begin` 在首个输入前执行一次，`end` 在流结束后执行一次
- `process` 块内 `$_` / `$input` 均可用：`$_` 是当前项，`$input` 是剩余流（在 `process` 内枚举会消费上游，慎用）
- `process` 块内输出（`Write-Output` / 隐式返回 / `yield` 等价物）写入下游管道
- 函数无 `process` 块时，管道输入被忽略（仅 `begin`/`end` 执行）

别名内含管道时，外部管道与别名管道直接拼接（未变）：

```
alias ll="get-childitem | sort by name"
ll | where size > 1MB
→ get-childitem | sort by name | where size > 1MB
```

### 11. 别名管理命令

| 命令 | 说明 |
|---|---|
| `Get-Alias` | 列出所有别名，可 `-Name <pattern>` 过滤 |
| `Set-Alias` | 临时设置别名（会话级） |
| `Remove-Alias` | 移除会话级别名 |
| `Export-Alias` | 导出当前别名到文件 |
| `Import-Alias` | 从文件导入别名 |

`Get-Alias` 输出：

```
Name    Command                              Source      Description
ll      get-childitem -l                     user        long listing
ls      get-childitem                        builtin     -
gp      get-itemproperty                     user        get properties
```

### 12. 函数管理命令

| 命令 | 说明 |
|---|---|
| `Get-Function` | 列出函数，可 `-Name <pattern>` 过滤；标记 `Source`（`session` / `project` / `user` / `toml-deprecated`） |
| `Set-Function` | 临时定义函数（会话级），接受函数名 + 函数体或 `.ops1` 文件路径 |
| `Remove-Function` | 移除会话级函数 |
| `Edit-Function` | 用 `$EDITOR` 打开函数源（若来自 `.ops1` 文件）编辑 |
| `Export-Function` | 导出指定函数到 `.ops1` 文件（未来） |

### 13. 项目级别名与函数

进入含 `.openshell/` 目录的子目录时，自动加载该目录的别名与函数：

```
project/
├── .openshell/
│   ├── aliases.toml       # 声明式别名（TOML）
│   ├── functions.toml     # 旧 TOML 函数（废弃，仍加载）
│   └── functions.ops1     # PowerShell 风格函数脚本（推荐）
└── ...
```

加载顺序（与 ADR-0041 §2 一致）：

1. 内置（命令系统、Provider 注册）
2. 内置别名（`[Verb(Aliases)]` 特性）
3. 内置命令（Cmdlet 注册）
4. 用户全局 `aliases.toml`
5. 用户全局 `functions.toml`（废弃格式）或 `functions.ops1`
6. 用户全局 `profile.openshell`
7. 项目级 `aliases.toml` / `functions.toml` / `functions.ops1`
8. 项目级 `profile.openshell`
9. REPL 循环 / GUI 主窗口

项目级别名优先级：用户全局 > 项目 > 内置（项目级覆盖内置但不覆盖用户全局）。函数同理。

### 14. 示例

#### 14.1 简单函数

```powershell
function Greet {
    param([string]$Name = "World")
    "Hello, $Name"
}

Greet              # → "Hello, World"
Greet -Name "Ada"  # → "Hello, Ada"
```

#### 14.2 管道过滤函数（filter 风格，参见 ADR-0012）

```powershell
function Get-LargeFile {
    param([int]$SizeMB = 1)
    process {
        if ($_.Size -gt ($SizeMB * 1MB)) {
            $_
        }
    }
}

get-childitem | Get-LargeFile -SizeMB 10
```

#### 14.3 begin/process/end 聚合管道输入

```powershell
function Measure-Directory {
    begin {
        $total = 0L
        $count = 0
    }
    process {
        $total += $_.Size
        $count++
    }
    end {
        [PSCustomObject]@{
            Count      = $count
            TotalBytes = $total
            AvgBytes   = if ($count -gt 0) { [math]::Round($total / $count, 2) } else { 0 }
        }
    }
}

get-childitem | Measure-Directory
```

#### 14.4 process 块内 try/catch

```powershell
function Copy-Safe {
    param(
        [Parameter(Mandatory, Position=0)] [string]$Source,
        [Parameter(Mandatory, Position=1)] [string]$Destination,
        [switch]$Force
    )
    process {
        try {
            Copy-Item -Path $Source -Destination $Destination -Force:$Force -ErrorAction Stop
            Write-Verbose "Copied $Source -> $Destination"
        }
        catch [System.IO.FileNotFoundException] {
            Write-Warning "Source not found: $Source"
        }
        catch {
            Write-Error "Copy failed: $_"
        }
    }
}
```

#### 14.5 递归函数

```powershell
function Get-Factorial {
    param([Parameter(Mandatory)] [int]$n)
    if ($n -le 1) { return 1 }
    return $n * (Get-Factorial -n ($n - 1))
}

Get-Factorial -n 5   # → 120
```

#### 14.6 ValueFromPipelineByPropertyName

```powershell
function Rename-Lower {
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string]$Name
    )
    process {
        Rename-Item -Path $Name -NewName $Name.ToLower()
    }
}

get-childitem | Rename-Lower
```

#### 14.7 Splatting 转发

```powershell
function Wrap-Copy {
    param(
        [string]$Source,
        [string]$Destination,
        [switch]$Force
    )
    process {
        # 把已绑定参数原样转发给 Copy-Item
        Copy-Item @PSBoundParameters
    }
}
```

### 15. `$args` 与 `$input`（与 ADR-0042 联动）

函数体内可用：

- `$args`：未被命名参数消费的剩余位置参数数组（ADR-0042 §2）
- `$PSBoundParameters`：本次调用成功绑定的命名参数 hashtable
- `$input`：上游管道输入 `IAsyncEnumerable<IItem>`，仅管道调用时有效
- `$_` / `$PSItem`：`process` 块内当前输入项
- `$1` / `$2` ...：位置参数简写（兼容旧 TOML 函数风格，由 lowering 注入）

别名内**不支持** `$args` / `$input`（别名是纯文本替换，无运行时作用域）。

### 16. 移除的限制（原 §10 修订）

原 ADR-0024 §10 "函数体 DSL 限制" 明确禁止多项能力，PowerShell 全兼容路线后**全部移除**：

- ❌ Removed："控制流（if/for/while/foreach/switch/try-catch）禁止" —— 现已允许（详见 ADR-0045）
- ❌ Removed："变量赋值（`$x = ...`）禁止" —— 现已允许（详见 ADR-0047）
- ❌ Removed："函数嵌套定义禁止" —— 现已允许，嵌套函数作用域为外层函数
- ❌ Removed："TOML-only 函数"要求 —— 现以 PowerShell `function` 为主机制，TOML `[[function]]` 降级为废弃的向后兼容格式
- ❌ Removed："函数体内禁止 `exit` / `return`" —— `return [<expr>]` 现已允许用于返回值；`exit` 仍受 ADR-0036 沙箱约束

需要超出函数语义的能力（如原生互操作、重型计算）仍推荐 C# 插件命令（ADR-0016），但日常组合逻辑现可在函数内表达。

## Alternatives Considered

1. **不支持用户自定义**：被否决，CLI 体验差。

2. **TOML-only 函数（原 ADR-0024 设计）**：被否决（2026-07-08）。理由：阻断 PowerShell 全兼容目标（与 ADR-0012 修订冲突），无法表达控制流 / 变量 / 异常处理 / `begin/process/end`，PowerShell 现有 `function` 脚本无法迁移；`ForEach-Object` / `Where-Object` 等需要的脚本块语义无法复用。原"避免变成脚本语言"的安全优势可由命令级沙箱（ADR-0036）替代获得，不须靠语法受限实现。

3. **PowerShell 完整 `function` + `CmdletBinding` 高级函数**：部分否决。`param()` + `begin/process/end` + 参数属性采纳；`CmdletBinding` 的 `SupportsShouldProcess` / `ConfirmImpact` 等高级特性留待 ADR-0048（Cmdlets）统一处理，函数首期不强制使用 `CmdletBinding`。

4. **嵌入脚本语言（Lua/JS/Python）**：被否决，引入外部依赖与安全风险，与 OpenShell 命令系统割裂。

5. **仅支持别名，不支持函数**：被否决，多语句组合与管道段是常见需求。

6. **别名用 AST 替换**：被否决，文本替换简单且足够，AST 替换需完整解析器；别名与函数已分层，复杂逻辑走函数。

7. **保留 TOML 函数并新增 PowerShell 风格，两者并列**：被否决，双轨制增加认知与维护成本；TOML `[[function]]` 标记废弃、内部 lowering 为 `function`，统一到单一求值路径。

## Consequences

### 优势

- **PowerShell 全兼容**：现有 PS `function` 脚本（`param()` / `begin/process/end` / 控制流 / 异常）可零成本迁移
- **表达力强**：函数内可写任意逻辑（控制流、变量、递归、异常），不再受"仅命令 + 管道"限制
- **与 ADR-0012 脚本块统一**：函数体与 `Where-Object { }` / `ForEach-Object { }` 共享脚本块引擎（ADR-0046），单一求值路径
- **管道段一等公民**：函数可作为 `IPipelineTransform` 节点，`begin/process/end` 覆盖流式处理需求
- **别名 / 函数职责清晰**：别名仅文本替换，函数承载组合逻辑，认知成本低
- **TOML 别名声明保留**：简单命令重命名仍用 TOML，零学习成本
- **向后兼容**：旧 `[[function]]` TOML 自动 lowering，不破坏既有配置

### 代价

- **安全模型下沉**：函数体现在执行任意代码（文件 IO、网络、进程、反射等由调用 cmdlet 触发），安全责任由语法层下沉到命令级沙箱（ADR-0036）。沙箱必须拦截危险操作，函数内 cmdlet 调用同样受约束。
- **依赖脚本块引擎（ADR-0046）**：M2 必须先交付脚本块基础，`begin/process/end` 与控制流才能工作
- **依赖变量系统（ADR-0047）**：参数绑定 / `$args` / `$PSBoundParameters` / 局部作用域需要变量系统支持（ADR-0042 revised）
- **依赖控制流（ADR-0045）**：`if/for/while/try-catch` 需要 Parser 与求值器支持
- **TOML `[[function]]` 废弃维护成本**：lowering 路径需长期保留，新增参数特性需同时维护两路
- **作用域语义复杂化**：函数局部作用域 + 嵌套函数 + 词法查找，需文档与错误信息友好
- **递归深度限制**：默认上限 100，深递归报 `CallDepthExceededException`，需文档说明

### 约束

- 别名展开必须是完整 token 匹配（`lsa` 不展开为 `ls`+`a`）
- 别名循环检测在 `set-alias` / 配置加载时执行
- 函数体必须可解析，否则拒绝加载并报 `ParseError`（ADR-0026）
- 项目级函数加载失败时降级到用户全局，不阻断启动
- 用户别名 / 函数覆盖内置命令时必须警告（启动时 + 首次使用时）
- 别名 / 函数名遵循 ADR-0023 命名规范：禁止以数字开头；别名禁止含 `-`（避免与 Verb-Noun 混淆）；函数名应为 `Verb-Noun` 形式
- 别名展开后的命令仍受 Verb-Noun 系统约束
- `Export-Alias` 默认不导出内置别名（仅用户自定义）
- 函数内 `return` 退出当前函数；`exit` 退出整个会话，受 ADR-0036 沙箱约束
- 函数递归调用深度上限默认 100，超过抛 `CallDepthExceededException`
- 函数体内 cmdlet 调用受 ADR-0036 沙箱约束，禁止逃逸操作（文件写、网络、进程）除非沙箱显式放行
- TOML `[[function]]` 加载时 lowering 为 `function`，`Source` 标记 `toml-deprecated`，首次调用打印一次废弃警告
- `param()` 块若存在必须是函数体第一个语句
- 单块体视为 `process` 块；无 `process` 块时管道输入被忽略
- 公共参数（`-Verbose` / `-Debug` / `-WhatIf` / `-Confirm` / `-ErrorAction` / `-WarningAction`）所有函数自动支持，行为与 cmdlet 一致
- `$args` 仅含未被命名参数消费的位置参数；`$PSBoundParameters` 仅含已成功绑定的命名参数
- 项目级别名 / 函数必须用 git 提交时排除敏感信息（不含凭据），凭据应通过 `ICredentialProvider`（ADR-0022 §5）访问
- `.ops1` 脚本文件以 UTF-8 编码读取，BOM 可选
- dot-source（`. file.ops1`）嵌套深度上限 5，必须检测循环引用
