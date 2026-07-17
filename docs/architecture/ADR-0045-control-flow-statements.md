# ADR-0045: 控制流语句（if / while / for / foreach / switch / try-catch）

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (REPL), ADR-0010 (Pipeline), ADR-0024 (Functions, revised), ADR-0026 (Error Model), ADR-0042 (Variables, revised), ADR-0046 (Script Blocks), ADR-0047 (Variable System)

## Context

OpenShell 此前明确禁止控制流语句：

- **ADR-0024 §10** 规定函数体仅支持"命令 + 管道"，不允许 `if` / `for` / `while`，理由是"避免变成脚本语言"，并在 §10 的约束中重申"函数体内禁止 `exit` / `return`（首期不支持返回值）"。
- **ADR-0041 §6** 在 profile 脚本语法表里把 `if` / `for` / `while` 标为 ❌，并把"profile 支持完整控制流"列为被否决的替代方案（§6 Alternatives 第 6 项），推荐用 `if-exists` 单命令表达条件分支。

这一禁令在 M1/M2 阶段是合理的：早期阶段优先稳定命令系统、Provider、管道、错误模型与变量系统，控制流会显著扩大解析器与运行时复杂度。但进入 M4 语言层后，禁令已成为 OpenShell 体验的硬性瓶颈：

1. **PowerShell 全兼容目标**：用户已确认方向为"PowerShell 全兼容"。PowerShell 脚本几乎都包含 `if` / `foreach` / `try-catch`，缺失这些构造 OpenShell 无法承载真实 PowerShell 脚本。
2. **基础语义缺失**：非平凡脚本必然需要条件分支、循环、异常处理。当前仅靠 `if-exists` 单命令（ADR-0041 §6）和 C# 插件 workaround，无法覆盖循环、switch、try-catch 等常见场景。
3. **函数能力受限**：ADR-0024 函数体被限定为"命令 + 管道"，复杂逻辑只能下沉到 C# 插件，破坏 shell 的脚本体验。
4. **profile 表达力不足**：跨平台条件分支（`$OS` 分支）、Host 类型分支（`$HOST`）当前只能用 `if-exists` 间接表达，不自然。
5. **错误处理薄弱**：ADR-0026 区分终止 / 非终止错误，但用户无 `try-catch` 可程序化捕获终止错误，仅能事后查 `$ERROR` / `get-error`。

参考 PowerShell 的控制流模型：C 风格关键字（`if` / `while` / `for` / `switch` / `try`）+ 语句块（`{ ... }`）+ 跳转语句（`break` / `continue` / `return` / `exit`），同时保留管道优先级与对象流语义。OpenShell 采纳该模型，作为 M4 语言层的核心。

本 ADR 将被以下 ADR 引用：

- **ADR-0012（修订）**：filter/sort/select DSL 与控制流的优先级关系。
- **ADR-0024（修订）**：函数体放开控制流限制（§10 失效）。
- **ADR-0046（Script Blocks）**：`{ ... }` 语句块的语义、作用域、求值规则。
- **ADR-0047（Variable System）**：变量作用域栈与控制流帧的联动（循环局部变量、catch 内 `$_`）。

## Decision

引入完整的 PowerShell 风格控制流语句，覆盖条件、循环、分支、异常、跳转五类构造。所有构造均为语句（statement），由 M4 新引入的 Parser（per ADR-0046）解析为 AST 节点，由 Evaluator 求值。控制流与 ADR-0010 管道模型双向集成：语句可作为管道节点，管道也可作为语句的表达式。

### 1. `if` / `elseif` / `else`

语法：

```powershell
if ($count -gt 10) {
    "many"
} elseif ($count -gt 5) {
    "some"
} else {
    "few"
}
```

语义：

- 条件表达式按"真值规则"求值为 `[bool]`（详见 §11 真值规则）。
- `if` 与 `elseif` 的条件表达式必须用 `(...)` 包裹，与 PowerShell 一致。
- `elseif` 可重复多次，`else` 至多一次且必须最后。
- 三个分支体均为脚本块（per ADR-0046）。
- 块的输出是块内最后一条语句的输出（详见 §12 输出语义）。
- `if` 作为语句求值时，输出第一个匹配分支的输出；无匹配且无 `else` 时输出 `$null`。

真值规则（参考 PowerShell，详见 §11）：

- 真值：非零数字、非空字符串、非空数组、`$true`。
- 假值：`0`、`""`、`@()`、`$null`、`$false`。
- 集合真值：单元素集合按元素真值；多元素集合恒真；空集合为假。

### 2. `while`

语法：

```powershell
while ($i -lt 10) {
    $i++
}
```

语义：

- 条件为真时反复执行块体。
- 条件求值在每次迭代前。
- 条件首次为假时块体零次执行。
- 块内 `break` 退出循环，`continue` 跳到下次条件判断。
- 块内最后一条语句的输出累计为循环输出（流式）。

### 3. `do - while` / `do - until`

语法：

```powershell
do {
    $i++
} while ($i -lt 10)

do {
    $i++
} until ($i -ge 10)
```

语义：

- `do { } while (cond)`：先执行块体，再判断 `cond`，为真则继续。
- `do { } until (cond)`：先执行块体，再判断 `cond`，为假则继续（即"直到 cond 为真停止"）。
- 块体至少执行一次。
- `while` 与 `until` 是逻辑对偶：`until (c)` 等价 `while (-not (c))`。

### 4. `for`

语法：

```powershell
for ($i = 0; $i -lt 10; $i++) {
    "Item $i"
}
```

语义：

- 三段式：`init` / `condition` / `increment`，以 `;` 分隔，整体用 `(...)` 包裹。
- 三段均**可选**：`for (;;) { }` 是合法的无限循环。
- `init` 在循环开始前执行一次；`condition` 每次迭代前求值（缺省视为 `$true`）；`increment` 每次迭代体执行后执行。
- `init` 与 `increment` 可以是逗号分隔的多语句：`for ($i = 0, $j = 10; $i -lt $j; $i++, $j--)`。
- `break` / `continue` 语义同 `while`。

### 5. `foreach`（statement form）

语法：

```powershell
foreach ($item in $collection) {
    $item.Name
}
```

语义：

- 迭代任何 `IEnumerable`（含 `IAsyncEnumerable<IItem>` 管道输出）。
- `$item` 是循环作用域局部变量（per ADR-0047 局部作用域），循环结束后离开作用域。
- 迭代变量在每次迭代开始时绑定当前元素。
- 块体输出累计为循环输出（流式，逐元素产出）。
- 迭代空集合：块体零次执行，输出为空。
- `break` / `continue` 语义同 `while`。

`foreach` 语句 vs `ForEach-Object` 管道 cmdlet：

- `foreach` 是**语句**，迭代变量是命名局部变量（`$item`），作用域明确。
- `ForEach-Object`（per ADR-0048）是**管道 cmdlet**，使用 `$_` 当前管道对象，每个对象流过时执行脚本块。
- 二者不能互换：`foreach` 不接管道输入，`ForEach-Object` 不能用作语句。
- 性能：`foreach` 对已物化的集合略快（无管道开销）；对 `IAsyncEnumerable` 流二者等价。

### 6. `switch`

基本语法：

```powershell
switch ($color) {
    "red"    { "stop" }
    "yellow" { "caution" }
    "green"  { "go" }
    default  { "unknown" }
}
```

模式：

```powershell
# 正则模式
switch -Regex ($input) {
    "^\d+$"    { "number" }
    "^[a-z]+$" { "letters" }
    default    { "other" }
}

# 通配符模式
switch -Wildcard ($filename) {
    "*.txt" { "text file" }
    "*.log" { "log file" }
}

# 文件模式：逐行读取文件并匹配
switch -File $path {
    "error" { $errors++ }
    default { $other++ }
}
```

语义：

- 模式标志（位标志，可组合）：默认 exact、`-Regex`、`-Wildcard`、`-File`。其中 `-File` 可与 `-Regex` / `-Wildcard` 叠加（`switch -File $path -Regex { ... }`），`-Regex` 与 `-Wildcard` 在匹配阶段二选一（同时出现时 `-Regex` 优先）。
- `-File <path>`：读取文件逐行作为 `$_` 输入，每行尝试匹配所有 case。
- 每个 case 是 `<pattern> { <block> }` 形式。
- `default` 块至多一个，所有 case 均不匹配时执行。
- **fall-through 语义**（PowerShell 风格）：默认不 break，匹配的 case 执行后**继续尝试后续 case**；用 `break` 显式退出 switch。
- 多 case 可同时匹配并执行（与 C / Java / C# 的 switch 不同，需用户文档强调）。
- 块内 `$_` 是当前输入值（per ADR-0042）。
- `break` 退出整个 switch；无 `break` 时 fall-through 到下一个 case。

模式匹配规则：

| 模式 | 匹配方式 |
|---|---|
| exact（默认） | 字符串相等比较（OrdinalIgnoreCase 默认；`-CaseSensitive` 切换） |
| `-Regex` | .NET 正则（`[regex]::IsMatch`） |
| `-Wildcard` | `WildcardPattern`（PowerShell 兼容，支持 `*` / `?` / `[a-z]`） |
| `-File` | 读取文件按行匹配，模式默认为 exact（可叠加 `-Regex` / `-Wildcard`） |

开关参数：

- `-CaseSensitive`：大小写敏感匹配（默认不敏感）。正交位标志，可与任一模式叠加。
- `-Regex` / `-Wildcard` / `-File`：模式选择位标志。
- 可组合：`switch -File $path -Regex { ... }`（`-File` 与 `-Regex` 叠加）、`switch -CaseSensitive -Wildcard { ... }`（`-CaseSensitive` 与 `-Wildcard` 叠加）。

### 7. `try` / `catch` / `finally`

语法：

```powershell
try {
    Get-Content $path -ErrorAction Stop
} catch [System.IO.FileNotFoundException] {
    "File not found: $($_.Exception.Message)"
} catch [System.UnauthorizedAccessException] {
    "Permission denied"
} catch {
    "Other error: $_"
} finally {
    Cleanup
}
```

语义：

- `try` 块：受保护代码。
- `catch [<ExceptionType>]`：类型过滤的 catch 块，可重复多次。
  - 类型从上到下匹配，第一个匹配的执行。
  - 无类型的 `catch` 是兜底（捕获所有 `OpenShellException` 及其派生）。
  - 类型名遵循 PowerShell 习惯：`[System.IO.FileNotFoundException]` / `[OpenShell.Errors.PermissionDeniedException]`。
- `finally` 块：至多一个，**总是执行**（无论是否抛出、是否捕获、是否 `break` / `return` / `exit`）。
- `$_` 在 `catch` 块内是 `ErrorRecord`（per ADR-0026），含 `Category` / `Message` / `Exception` / `TargetPath` 等字段。
- 异常类型与 `OpenShellException` 子类的映射：`ItemNotFoundException` / `PermissionDeniedException` / `ProviderNotFoundException` 等均可作为 catch 类型参数。

`-ErrorAction Stop` 升级：

- ADR-0026 §13 定义 `ErrorAction`：`Continue`（默认）/ `Stop` / `SilentlyContinue`。
- `-ErrorAction Stop` 将非终止错误升级为终止错误（抛 `OpenShellException`），被 `try-catch` 捕获。
- 不带 `-ErrorAction Stop` 的非终止错误**不**触发 `catch`，仅写入 `IErrorStream`（用户通过 `$ERROR` / `get-error` 查阅）。

`throw` 关键字：

- `throw <expr>` 抛出终止错误。
- `throw "message"`：等价 `throw [OpenShellException]::new("message")`，包装为 `ErrorRecord` 写入错误流。
- `throw $ErrorRecord`：直接抛出已有 `ErrorRecord`。
- `throw`（无表达式）：若 `catch` 块内，重抛当前 `$_`；否则抛"空 throw"错误。

`trap`（PowerShell legacy）：

- **不支持** `trap { ... }` 关键字。`trap` 是 PowerShell 早期异常机制，与 `try-catch` 语义重叠且优先级复杂（trap 作用于作用域，try-catch 作用于块）。
- 需要 catch 异常请用 `try-catch`。
- 解析器遇到 `trap` 关键字报 `ParseError`，提示用户迁移到 `try-catch`。

### 8. `break` / `continue`

语义：

- `break`：退出最内层 `while` / `do-while` / `do-until` / `for` / `foreach` / `switch`。
- `continue`：跳过最内层循环当前迭代，进入下次迭代（对 `switch` 等价于跳到下一个 case）。
- 在非循环 / 非 switch 上下文中使用 `break` / `continue` 报 `ParseError`。
- `finally` 块在 `break` / `continue` 转移控制前**先执行**。

带标签形式（参考 PowerShell）：

```powershell
:OuterLoop foreach ($i in 1..3) {
    foreach ($j in 1..3) {
        if ($j -eq 2) { break :OuterLoop }
        "i=$i j=$j"
    }
}
```

- 标签前缀 `:` 定义在 `foreach` / `while` / `for` / `do-while` 之前。
- `break :Label` / `continue :Label` 跨多层循环跳转。
- 标签使用较少，但保留与 PowerShell 兼容。

### 9. `return`

语义：

- `return [<expr>]`：退出当前函数或脚本块，返回可选值。
- `return $value`：返回 `$value`（按 ADR-0024 函数返回值机制，下文）。
- 裸 `return`：返回 `$null`。
- 在 `process` 块内（per ADR-0046 Script Blocks）：`return` 仅退出当前管道迭代，不退出整个函数。
- 在脚本顶层（非函数）：`return` 退出整个脚本，返回值给调用者。
- `finally` 块在 `return` 转移控制前**先执行**。

与 ADR-0024 函数返回值联动：

- ADR-0024 §10 此前禁止函数体内 `return`，本 ADR 修订：函数体内允许 `return`。
- 函数输出 = 块内所有未捕获的输出累计 + `return` 表达式（若有）。
- 即：未显式 `return` 的函数，输出是块内语句产出的流（PowerShell 风格）。

### 10. `exit`

语义：

- `exit [<code>]`：退出当前脚本或 REPL 会话。
- `exit 42`：设置 `$LASTEXITCODE = 42`（per ADR-0042）并退出。
- 裸 `exit`：`$LASTEXITCODE` 保持上一条命令的值（PowerShell 习惯：`exit` 不重置）。
- 在 REPL 顶层：退出进程，退出码 = `$LASTEXITCODE`。
- 在脚本内：停止脚本执行，返回调用者（dot-source 的脚本则返回 REPL）。
- `finally` 块在 `exit` 转移控制前**先执行**（与 PowerShell 一致）。

退出码语义沿用 ADR-0026 §6（0=成功，1=一般错误，…，130=SIGINT）。

### 11. 真值规则

OpenShell 真值规则与 PowerShell 一致：

| 类型 | 真值 | 假值 |
|---|---|---|
| `[bool]` | `$true` | `$false` |
| `[int]` / `[long]` / `[double]` | 非 0 | `0` / `0.0` |
| `[string]` | 非空（`""` 之外的任何串） | `""` / `$null` |
| `[array]` / 集合 | 非空 | `@()` / 空集合 |
| `[hashtable]` | 非空 | `@{}` |
| `[object]` / `IItem` | 非 `$null` | `$null` |
| 单元素集合 | 按元素真值 | — |
| 多元素集合 | 真（不论元素） | — |

特殊规则：

- 字符串 `"0"` 是**真值**（非空字符串）—— 与 C 不同，需在文档强调。
- 数字 `0` 是假值，但字符串 `"0"` 是真值。
- `$null` 在数值上下文视为 `0`，在布尔上下文视为假。

### 12. 输出语义

PowerShell 风格：语句的输出是语句求值过程中"未被赋值或捕获的表达式值"。

- 块 `{ expr1; expr2; expr3 }` 的输出是 `expr1, expr2, expr3` 三者输出的流（拼接）。
- `if` 语句的输出是匹配分支块的输出；无匹配则空。
- `while` / `for` / `foreach` 的输出是所有迭代块输出的拼接流。
- `try` 块的输出是 try 体输出，或匹配 catch 块的输出。
- `switch` 的输出是所有匹配 case 块输出的拼接流。
- 显式 `return $value`：`$value` 追加到函数输出流。

显式赋值不输出：

```powershell
$x = 5      # 不输出（赋值语句）
$x          # 输出 5
```

### 13. 语句分隔

- **换行**终止语句（PowerShell 风格）。
- **`;`** 可在一行内分隔多条语句：`$a = 1; $b = 2; $c = $a + $b`。
- 行尾 `;` 允许（no-op），不报错。
- 续行：未闭合的 `{` / `(` / `[` / `"` 触发 REPL 续行提示（per ADR-0008 多行输入扩展）。

### 14. Parser 集成

- 控制流语句由 M4 新引入的 Parser（per ADR-0046 实现）解析为 AST 节点。
- Parser 是递归下降解析器，含表达式优先级表（管道 `|` > 比较运算 > 逻辑运算 > 赋值）。
- 块 `{ ... }` 内可包含任意语句序列（条件、循环、赋值、表达式、管道）。
- 块嵌套无深度限制（实际限制 256 层防栈溢出）。
- REPL 多行输入（per ADR-0008）扩展：未闭合 `{` / `(` / `[` / `"` 触发续行提示 `...`。
- AST 是强类型节点树，便于调试器、LSP、formatter 复用。

AST 节点（节选）：

```csharp
public abstract record StatementAst;
public sealed record IfStatementAst(
    ExpressionAst Condition,
    ScriptBlockAst Body,
    IReadOnlyList<ElseIfClause> ElseIfClauses,
    ScriptBlockAst? ElseBody) : StatementAst;
public sealed record WhileStatementAst(ExpressionAst Condition, ScriptBlockAst Body) : StatementAst;
public sealed record DoWhileStatementAst(ScriptBlockAst Body, ExpressionAst Condition, bool IsUntil) : StatementAst;
public sealed record ForStatementAst(
    StatementAst? Init, ExpressionAst? Condition, ExpressionAst? Increment,
    ScriptBlockAst Body) : StatementAst;
public sealed record ForeachStatementAst(
    string VariableName, ExpressionAst Collection, ScriptBlockAst Body) : StatementAst;
public sealed record SwitchStatementAst(
    ExpressionAst Test, IReadOnlyList<SwitchCase> Cases,
    IReadOnlyList<StatementAst>? Default,
    SwitchFlags Flags) : StatementAst;

public sealed record SwitchCase(ExpressionAst Pattern, IReadOnlyList<StatementAst> Body);

[Flags]
public enum SwitchFlags
{
    None = 0,
    Wildcard = 1,      // -wildcard
    Regex = 2,         // -regex
    CaseSensitive = 4, // -case
    File = 8,          // -file
}
public sealed record TryStatementAst(
    ScriptBlockAst Body,
    IReadOnlyList<CatchClause> CatchClauses,
    ScriptBlockAst? FinallyBody) : StatementAst;
public sealed record BreakStatementAst(string? Label) : StatementAst;
public sealed record ContinueStatementAst(string? Label) : StatementAst;
public sealed record ReturnStatementAst(ExpressionAst? Value) : StatementAst;
public sealed record ExitStatementAst(ExpressionAst? Code) : StatementAst;
public sealed record ThrowStatementAst(ExpressionAst? Value) : StatementAst;
```

### 15. Pipeline 集成

控制流与 ADR-0010 管道模型双向集成。

**语句作为管道源**：

```powershell
foreach ($i in 1..10) { $i } | Where-Object { $_ -gt 5 }
```

- `foreach` 语句的输出（流式）作为管道 Source。
- PipelineExecutor（ADR-0010 §2）新增"语句 Source"分支：检测到语句 AST 时求值为 `IAsyncEnumerable<IItem>`，复用现有 Source → Transform* → Sink 流式编排。
- `if` / `switch` / `try` 同理：块输出可作管道源。

**语句作为管道阶段**：

- `if` / `foreach` / `switch` / `try` 块可作为 `Where-Object` / `ForEach-Object` / `Select-Object` 的脚本块参数（per ADR-0046）。
- 块内 `$_` 是当前管道对象（per ADR-0042）。

**管道优先级**：

- 管道 `|` 绑定优先级**高于**语句关键字。
- 即 `Get-Process | Sort-Object CPU | foreach { $_.Name }` 解析为单条管道语句，`foreach` 此处是 cmdlet 不是语句。
- 要让 `foreach` 作为语句，必须独占一行或显式括号包裹。

**示例**：

```powershell
# foreach 语句作 Source
foreach ($p in Get-Process) {
    if ($p.CPU -gt 100) { $p.Name }
} | Sort-Object

# if 作 Source
if (Test-Path $file) { Get-Content $file } | Select-Object -First 10

# try 作 Source
try {
    Invoke-WebRequest $url
} catch {
    "fallback"
} | ConvertFrom-Json
```

### 16. 错误模型集成

与 ADR-0026 错误模型联动：

- **终止错误**：抛 `OpenShellException`（或派生类），被 `catch` 捕获。
- **非终止错误**：写入 `IErrorStream`（`$ERROR` / `$ERRORS`），不触发 `catch`。
- **`-ErrorAction Stop`**：将非终止错误升级为终止错误，触发 `catch`。
- **`-ErrorAction SilentlyContinue`**：静默跳过，不写错误流。
- **`-ErrorAction Continue`**（默认）：写错误流，继续。
- **未捕获的终止错误**：传播到 host，写入错误流，命令 / 脚本退出，退出码按 ADR-0026 §6。

`$_` 在 `catch` 块：

- 是 `ErrorRecord`（per ADR-0026 §1），含全部字段。
- `$_.Exception` 是底层 .NET 异常。
- `$_.Category` / `$_.Message` / `$_.TargetPath` 直接可读。
- `catch` 块内 `throw`（无参）重抛当前 `$_`。

`$?` 与 `$LASTEXITCODE`：

- `try` 块成功完成：`$? = $true`，`$LASTEXITCODE` 不变。
- `catch` 块执行：`$?` 在 catch 入口为 `$false`（异常被捕获），catch 块内最后一条命令的 `$?` 覆盖。
- `finally` 块不修改 `$?` / `$LASTEXITCODE`（除非 finally 内显式执行命令）。

### 17. 运算符链（PowerShell 7+）

`&&` / `||` 链式运算符：

```powershell
Test-Path $file && Get-Content $file
Get-Content $file || Write-Error "File not found"
```

语义：

- `cmd1 && cmd2`：`cmd1` 成功（`$? = $true`）时执行 `cmd2`，否则跳过。
- `cmd1 || cmd2`：`cmd1` 失败（`$? = $false`）时执行 `cmd2`，否则跳过。
- 优先级低于管道 `|`，高于赋值 `=`。
- 链式：`a && b && c` / `a || b || c` / `a && b || c`（左结合）。
- 仅基于 `$?`，不看 `$LASTEXITCODE`（与 bash 不同，bash 看退出码）。

与控制流的关系：

- `&&` 是 `if ($?) { ... }` 的语法糖。
- `||` 是 `if (-not $?) { ... }` 的语法糖。
- 不引入新 AST 节点：解析为 `IfStatementAst`，条件是 `$?`（或 `!$?`）。

### 18. 保留字

以下关键字为保留字，禁止作为命令名 / 别名 / 函数名 / 变量名：

```
if  elseif  else  while  do  until  for  foreach  switch  try  catch  finally
break  continue  return  exit  throw  trap（保留，不支持）
in  default  filter（保留）
```

- 关键字**大小写不敏感**：`IF` / `If` / `if` 等价。
- 与 ADR-0024 别名机制联动：禁止 `set-alias if "..."`，启动期报 `ConfigurationError`。
- 与 ADR-0023 命令清单联动：禁止注册名为关键字的命令。

## Examples

### 1. 递归目录扫描 + try-catch 处理权限错误

```powershell
function Scan-LargeFiles {
    param($root, $minMB = 100)
    $threshold = $minMB * 1MB
    $results = @()
    try {
        foreach ($file in Get-ChildItem -r $root) {
            try {
                if ($file.Length -gt $threshold) {
                    $results += $file
                }
            } catch [System.UnauthorizedAccessException] {
                # 单文件权限错误，跳过
                continue
            }
        }
    } catch [System.UnauthorizedAccessException] {
        Write-Warning "Cannot access root: $root"
    } finally {
        # 输出结果
        $results | Sort-Object Length -Descending | Select-Object -First 10
    }
}
```

### 2. 批处理管道 + foreach + switch

```powershell
$errors = 0; $warns = 0; $infos = 0; $other = 0
foreach ($line in Get-Content $logFile) {
    switch -Regex ($line) {
        "^\[(\w+)\] ERROR" { $errors++ }
        "^\[(\w+)\] WARN"  { $warns++ }
        "^\[(\w+)\] INFO"  { $infos++ }
        default            { $other++ }
    }
}
"Errors: $errors  Warns: $warns  Infos: $infos  Other: $other"
```

### 3. 条件管道 + if + &&

```powershell
if (Test-Path $config) {
    Get-Content $config | ConvertFrom-Json
} else {
    @{}  # 默认空配置
}
```

或用 `&&` / `||` 简化：

```powershell
Test-Path $config && (Get-Content $config | ConvertFrom-Json) || @{}
```

### 4. 循环 + break / continue

```powershell
$found = $null
foreach ($item in $collection) {
    if (-not $item.IsActive) { continue }
    if ($item.Name -eq $target) {
        $found = $item
        break
    }
}
if ($found) { "Found: $($found.Name)" } else { "Not found" }
```

### 5. switch 正则模式分类

```powershell
switch -Regex ($userAgent) {
    "MSIE (\d+)"     { "IE $($matches[1])" ; break }
    "Firefox/(\d+)"  { "Firefox $($matches[1])" ; break }
    "Chrome/(\d+)"   { "Chrome $($matches[1])" ; break }
    "Safari"          { "Safari" ; break }
    default           { "Unknown: $userAgent" }
}
```

### 6. try-catch-finally 完整异常处理

```powershell
function Copy-Safe {
    param($src, $dst)
    $copied = 0
    try {
        foreach ($file in Get-ChildItem -r $src) {
            try {
                Copy-Item $file.FullName $dst -ErrorAction Stop
                $copied++
            } catch [System.IO.IOException] {
                Write-Warning "IO error on $($file.Name): $($_.Exception.Message)"
            } catch [System.UnauthorizedAccessException] {
                Write-Warning "Permission denied: $($file.FullName)"
            }
        }
    } catch {
        Write-Error "Unexpected: $_"
    } finally {
        Write-Host "Copied $copied files from $src to $dst"
    }
}
```

### 7. for 循环 + do-until 重试

```powershell
# 二分查找
function Binary-Search {
    param($arr, $target)
    $lo = 0
    $hi = $arr.Length - 1
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ($arr[$mid] -eq $target) { return $mid }
        elseif ($arr[$mid] -lt $target) { $lo = $mid + 1 }
        else { $hi = $mid - 1 }
    }
    return -1
}

# do-until 重试
$attempts = 0
do {
    $attempts++
    $ok = Test-Connection $server -Quiet
} until ($ok -or $attempts -ge 5)
```

### 8. 嵌套循环 + 标签 break

```powershell
:Matrix foreach ($i in 1..10) {
    foreach ($j in 1..10) {
        $product = $i * $j
        if ($product -gt 50) { break :Matrix }
        Write-Host "$i x $j = $product"
    }
}
```

## Alternatives Considered

1. **保持原禁令（不支持控制流）**：被否决（2026-07-08）。理由：
   - 用户已确认 PowerShell 全兼容方向。
   - 控制流是 shell 脚本的基础设施，缺失导致 OpenShell 仅能作为命令行工具，无法承载真实脚本。
   - ADR-0024 §10 与 ADR-0041 §6 的禁令在 M1/M2 阶段合理（优先稳定核心系统），但 M4 语言层必须放开。
   - 原"if-exists 单命令"workaround 无法覆盖循环、异常、switch 等场景。

2. **仅 `if-exists` workaround 命令扩展**：被否决。
   - ADR-0041 §6 已引入 `if-exists`，仅支持单 path 条件分支。
   - 无法表达循环、switch、try-catch、break/continue、return、exit。
   - 扩展 `if-exists` 到 `for-exists` / `while-exists` 会让命令系统臃肿，违反 ADR-0004 的"命令职责单一"原则。

3. **Bash 风格控制流**：被否决。
   - 语法不同：bash 用 `if/then/fi`、`for/do/done`，与 PowerShell 的 `if/elseif/else { }` 不兼容。
   - 用户已选 PowerShell 全兼容方向，bash 风格会破坏兼容性。
   - bash 用管道字节流，与 ADR-0010 对象流模型冲突。

4. **嵌入 C# 脚本（CSI / Roslyn）**：被否决。
   - 语法不同：C# 用 `if (...) { } else { }` 语法接近 PowerShell，但类型系统、语句分隔、管道语义差异大。
   - 引入 Roslyn 依赖（数十 MB），编译开销大。
   - 与 OpenShell 命令系统、管道、变量系统割裂，无法直接 `foreach ($item in Get-ChildItem)`。

5. **嵌入 Lua / JS / Python 脚本引擎**：被否决。
   - 引入外部依赖与安全风险（沙箱逃逸、库生态）。
   - 语法与 PowerShell 不兼容，用户需学习两套语法。
   - 与 OpenShell 命令系统集成成本高。

6. **仅支持 if / foreach，不支持 try-catch**：被否决。
   - try-catch 是 PowerShell 脚本的基础设施，错误处理缺失无法承载生产脚本。
   - ADR-0026 错误模型已就绪，try-catch 是其程序化消费侧的必要补充。

7. **PowerShell 完整 `trap` 机制 + try-catch**：被否决。
   - `trap` 是 PowerShell 早期异常机制，与 try-catch 语义重叠，优先级规则复杂（trap 作用于作用域，try-catch 作用于块）。
   - 现代 PowerShell 脚本普遍使用 try-catch，trap 仅在 legacy 脚本出现。
   - 解析器遇到 `trap` 报 ParseError，提示用户迁移。

8. **强类型控制流（要求条件必须为 `[bool]`）**：被否决。
   - PowerShell 真值规则宽松（字符串、数字、集合均可参与条件），强制 `[bool]` 会破坏兼容性。
   - 真值规则与 PowerShell 一致（见 §11），降低用户学习成本。

## Consequences

### 优势

- **PowerShell 全兼容**：真实 PowerShell 脚本可在 OpenShell 直接运行（含 if/foreach/switch/try-catch）。
- **函数表达力**：ADR-0024 函数体放开控制流后，复杂逻辑可直接用 shell 脚本表达，无需下沉到 C# 插件。
- **profile 表达力**：ADR-0041 profile 脚本可写跨平台 / 跨 Host 条件分支，无需 workaround。
- **错误处理闭环**：ADR-0026 终止 / 非终止错误模型 + try-catch + throw 形成完整错误处理闭环，用户可程序化捕获与恢复。
- **管道与控制流双向集成**：语句可作管道源，管道可作语句表达式，表达力接近 PowerShell。
- **AST 强类型化**：解析器产出 AST 节点树，便于调试器、LSP、formatter、coverage 工具复用。
- **关键字大小写不敏感**：与 PowerShell 一致，降低用户记忆负担。

### 代价

- **解析器复杂度显著上升**：从 M2 的"按 `|` 分段 + 反射参数解析"（见 `PipelineExecutor.SplitPipeline`）升级为递归下降 AST 解析器，含表达式优先级表、语句上下文、块作用域。
- **AST 节点众多**：13 个语句节点 + 表达式节点 + 块节点，调试与测试覆盖成本高。
- **求值器需要控制流帧**：break / continue / return / exit 需要异常式的控制流信号（或等价机制），跨 finally 块的语义需小心实现。
- **性能开销**：每条语句求值约 1ms 开销（AST 遍历 + 作用域查找），对密集循环脚本可能感知（10k 次迭代 = 10s 额外）。缓解：JIT 缓存 AST 求值结果（M5+）。
- **真值规则易踩坑**：`"0"` 是真值、`0` 是假值，与 C / C# 不同，需文档强调。
- **switch fall-through 易混淆**：与 C / Java / C# switch（默认 break）相反，PowerShell switch 默认 fall-through，需用户文档强调。
- **`trap` 不支持的兼容性缺口**：legacy PowerShell 脚本含 `trap` 的需用户迁移到 try-catch，无法零成本兼容。

### 约束

- 所有关键字（§18）为保留字，禁止作为命令名 / 别名 / 函数名 / 变量名。
- 关键字**大小写不敏感**：`IF` / `If` / `if` 等价，与 PowerShell 一致。
- 控制流块体必须是脚本块（`{ ... }`），禁止裸语句：`if ($x) "yes"` 不合法（PowerShell 允许，OpenShell 首期要求块以简化解析）。
- 条件表达式必须用 `(...)` 包裹：`if $x { }` 不合法，必须 `if ($x) { }`。
- `for` 三段式以 `;` 分隔，整体用 `(...)` 包裹。
- `foreach` 迭代变量是循环作用域局部变量，循环结束后离开作用域（per ADR-0047）。
- `switch` 默认 fall-through，需显式 `break` 退出（与 C / C# 相反，需文档强调）。
- `try` 块至少含一个 `catch` 或 `finally`，不能裸 `try { }`。
- `catch` 类型必须是 `OpenShellException` 派生类或 `System.Exception` 派生类，否则 ParseError。
- `finally` 块总是执行，即使 try / catch 内有 `break` / `continue` / `return` / `exit` / `throw`。
- `break` / `continue` 必须在循环或 switch 上下文内，否则 ParseError。
- `return` 仅在函数或脚本块内有效，REPL 顶层裸 `return` 报 ParseError。
- `exit` 在 REPL 顶层退出进程，在脚本内退出脚本。
- `throw` 抛出的对象被包装为 `ErrorRecord` 写入 `IErrorStream`，`$_` 在 catch 内是该 `ErrorRecord`。
- `trap` 关键字不支持，解析器遇到报 ParseError，提示用户迁移到 try-catch。
- `&&` / `||` 仅基于 `$?`，不看 `$LASTEXITCODE`（与 bash 不同）。
- 真值规则按 §11 表，字符串 `"0"` 是真值（与 C 不同，需文档强调）。
- Parser 必须产出强类型 AST 节点树，便于调试器、LSP、formatter 复用。
- 错误信息必须含源行号 / 列号，便于调试（per ADR-0026 错误模型扩展）。
- AST 节点求值支持 `CancellationToken` 透传，Ctrl+C 可中断循环（per ADR-0008 Ctrl+C 处理）。
- REPL 多行输入扩展：未闭合 `{` / `(` / `[` / `"` 触发续行提示（per ADR-0008 多行输入扩展）。
- 块嵌套深度上限 256，防栈溢出。
- `foreach` 语句与 `ForEach-Object` 管道 cmdlet 是不同构造，禁止混用（per ADR-0048 区分）。
- 控制流语句的输出按 §12 输出语义拼接为流，可作管道 Source。
- 控制流块内可访问外层作用域变量（per ADR-0047 作用域链），但赋值默认进当前作用域。
- `catch` 块内 `$_` 是 `ErrorRecord`（per ADR-0026），`$_.Exception` 是底层异常。
- 本 ADR 修订 ADR-0024 §10（函数体放开控制流）、ADR-0041 §6（profile 放开控制流）、ADR-0042 §2（变量系统扩展局部作用域）。
- 所有控制流构造在 M4 语言层落地，M3 不实现（避免 M3 阶段核心系统未稳就引入复杂解析器）。
