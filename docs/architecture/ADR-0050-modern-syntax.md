# ADR-0050: OpenShell Modern Syntax (.osh) — 现代语法规范

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (REPL), ADR-0010 (Pipeline), ADR-0024 (Functions, revised), ADR-0042 (Automatic Variables, revised), ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0047 (Variable System), ADR-0048 (Cmdlets)
- **Implementation Status**: M4 已实现 (2026-07-08): §1-§10 现代语法 (fn/match/elif/lambda/字面量/r""/三引号/?.)、#lang REPL 切换、import 模块加载、三引号缩进剥离。Open Questions (§11) 中的 async/await、深度类型系统、macro、高级模式匹配、模块系统、运算符重载等待批6 独立 ADR。

## Context

OpenShell 的核心目标是「PowerShell 全兼容」+「现代语法」。ADR-0045 / ADR-0046 / ADR-0047 / ADR-0042 已经把控制流、脚本块、变量系统、自动变量的运行时语义完整定义下来，对齐 PowerShell 5.1+ 的行为。但 PowerShell 历史包袱让原生语法在多个维度上不够现代化，用户已明确表态："原版 PowerShell script 写起来太丑了，不好看，然后也不够简洁"，并希望"整体上更像 Python 会不会好一点，甚至比 Python 更现代更好用更简洁"。

### PowerShell 历史包袱

PowerShell 语法诞生于 2006 年，受当时 cmdlet 参数式风格影响深远，遗留了若干书写负担：

1. **参数式运算符冗长难记**：`-eq` / `-ne` / `-gt` / `-lt` / `-ge` / `-le` / `-and` / `-or` / `-not` 等运算符全部以 `-` 前缀书写，与 C / Java / Python / JavaScript / Rust / Kotlin 等主流语言完全不同。用户需要记忆"参数式比较符"这一独有概念，跨语言迁移成本高。`-not` 尤其反直觉（其他语言用 `!`），`-and` / `-or` 也无法与短路运算符 `&&` / `||` 的肌肉记忆对齐。
2. **函数语法噪音大**：`function Greet { param([string]$name = "World") "Hello, $name" }` 把函数名、参数块、参数类型、参数名、默认值、返回类型分散在 `function` / `param` / `[type]` / `$name` / `=` / 隐式返回等多个 token 中，参数声明必须独占 `param(...)` 块，无法像 Kotlin `fun greet(name: String = "World"): String` 那样在一行内表达签名。
3. **字面量前缀丑**：数组 `@(1, 2, 3)` 与哈希 `@{ k = v; k2 = v2 }` 都以 `@` 前缀书写，与 JSON / JavaScript / Python 的 `[1, 2, 3]` / `{ "k": v }` 视觉差异大。`@` 前缀还与 splatting `@params` 的 `@` 符号过载，初学者容易混淆。
4. **`$_` 当前对象符号过载**：`$_` 在 PowerShell 中承担"当前管道对象"语义，但 `$` 前缀让人误以为是普通变量。Python 用 `_`、Kotlin 用 `it`、C# LINQ 用 `_`，都更简洁。`$_.Property` 的属性访问也较长。
5. **`if ($x) { }` 括号冗余**：PowerShell 强制条件表达式用 `(...)` 包裹（见 ADR-0045 §1），与 Python `if x:` / Rust `if x { }` / Kotlin `if (x) { }` 的现代惯例不完全一致（Kotlin 虽有括号但类型推导更彻底）。
6. **`switch` fall-through 反直觉**：ADR-0045 §6 明确 PowerShell `switch` 默认 fall-through，与 C / Java / C# 相反，需用户文档强调。Rust `match` 默认非 fall-through 更符合现代语言直觉。
7. **`foreach ($x in $col) { }` 与 `for` 分裂**：PowerShell 把 `for` 三段式与 `foreach` 集合迭代分成两个关键字，Python / Rust / Kotlin 都用 `for x in col` 统一表达。
8. **here-string `@" ... "@` 啰嗦**：多行字符串需 `@"` 开头、`"@` 结尾且闭合标记必须在行首，与 Kotlin `"""..."""` / Python `"""..."""` 的三引号风格相比视觉负担重。

### 双语法决策

为同时满足"PowerShell 全兼容"与"现代语法"两个目标，OpenShell 采纳**双语法架构**：

- **PowerShell 兼容模式（`.ps1`）**：完全兼容 PowerShell 语法，用于运行已有 PowerShell 脚本、迁移 legacy 代码、与 PowerShell 生态互通。
- **现代语法（`.osh`）**：新定义的现代化语法，吸收 Python 的简洁、Rust / Kotlin 的类型推导、PowerShell 的对象流语义，作为 OpenShell 的主推语法。

两种语法经各自 parser 编译到**同一组 AST 节点**（per ADR-0045 §14 + ADR-0046 §10），evaluator 不感知语法差异。这意味着现代语法**不改变任何运行时语义**——作用域栈、变量查找、脚本块闭包、控制流真值规则、管道对象流、错误模型等全部沿用 ADR-0042 / ADR-0045 / ADR-0046 / ADR-0047 已固化的语义。现代语法只是这些语义的"另一种语法糖"。

### 与已有 ADR 的关系

本 ADR 不修订以下 ADR 的语义，仅提供等价的现代书写形式：

- **ADR-0045（Control Flow）**：`if` / `while` / `for` / `foreach` / `switch` / `try` / `break` / `continue` / `return` / `throw` 的 AST 节点（§14）与求值规则（§11 / §12）不变，现代语法仅改变关键字拼写（`elseif` → `elif`、`switch` → `match`）与括号要求。
- **ADR-0046（Script Blocks）**：`{ ... }` 脚本块、`begin/process/end` 三段、`param()` 块、闭包捕获、`&` 调用运算符的语义不变，现代语法新增箭头函数 `x => x * 2` 作为脚本块的简写。
- **ADR-0047（Variable System）**：作用域栈、类型转换规则、成员访问反射、子表达式、splatting 不变，现代语法仅改变字面量书写形式（`@(...)` → `[...]`、`@{...}` → `{...}`）与类型注解位置（`[int]$x` → `x: int`）。
- **ADR-0042（Automatic Variables）**：`$_` / `$args` / `$?` / `$LASTEXITCODE` / `$PSBoundParameters` / `$input` 等自动变量在两种语法下都可用；现代语法额外提供 `$` 作为 `$_` 的简写别名。

### 风格定位

现代语法的目标是"比 Python 更现代更好用更简洁"，具体吸收：

- **Python 的简洁**：`for x in col`、`elif`、`match`、缩进友好（但不强制缩进，保留 `{ }` 块）。
- **Rust / Kotlin 的类型推导**：`fn greet(name: string) -> string`，返回类型可省略由推导得出。
- **C# / Kotlin 的现代运算符**：`?.` / `?[]` null 条件、`??` null 合并、`? :` 三元。
- **JSON / JavaScript 的字面量**：`[1, 2, 3]` 数组、`{ k: v }` 哈希。
- **PowerShell 的对象流语义**：管道 `|`、`$_` 当前对象、对象属性访问、`ForEach-Object` / `Where-Object` 等命令式管道体验不变。

## Decision

引入 `.osh` 现代语法，与 `.ps1` PowerShell 兼容语法并行存在，编译到同一 AST。以下分 10 节定义现代语法的全部细节。

### 1. 双语法架构

#### 1.1 文件后缀与默认判定

| 文件后缀 | 语法模式 | 用途 |
|---|---|---|
| `.osh` | 现代语法（默认） | OpenShell 主推语法，新脚本首选 |
| `.ps1` | PowerShell 兼容 | 运行已有 PowerShell 脚本、迁移 legacy 代码 |

- 文件后缀是默认语法判定的依据：`.osh` 文件按现代语法解析，`.ps1` 文件按 PowerShell 兼容语法解析。
- REPL 默认现代语法，与 `.osh` 文件一致；`#lang ps1` 指令切换到 PowerShell 兼容模式，`#lang osh` 切回现代模式（per ADR-0008 REPL 扩展）。
- 无后缀或未识别后缀的文件：默认按现代语法解析，报 `ParseWarning` 提示用户显式指定后缀。

#### 1.2 同一 AST

两种语法的 source code 经各自 parser 编译到**同一组** `StatementAst` / `ExpressionAst` 节点（per ADR-0045 §14 + ADR-0046 §10）。例如：

- 现代语法 `if x > 0 { }` 与 PowerShell `if ($x -gt 0) { }` 编译到同一个 `IfStatementAst`。
- 现代语法 `[1, 2, 3]` 与 PowerShell `@(1, 2, 3)` 编译到同一个 `ArrayLiteralExpressionAst`。
- 现代语法 `fn greet(name: string) { }` 与 PowerShell `function greet { param([string]$name) }` 编译到同一个 `FunctionDefinitionAst`（本 ADR 新引入；函数语义由 ADR-0024 revised 定义）。

Evaluator 仅消费 AST 节点，不感知语法来源。这意味着调试器、LSP、formatter、coverage 工具等基于 AST 的工具链天然支持两种语法。

#### 1.3 混用与块切换

- `.osh` 文件内可用 `#lang ps1 { ... }` 块嵌入 PowerShell 代码，用于粘贴 legacy PS 脚本片段。
- `.ps1` 文件内可用 `#lang osh { ... }` 块嵌入现代语法代码（罕见，但对称支持）。
- 块切换语法是 `#lang <mode> { <body> }`，`<body>` 内按指定模式解析，闭合 `}` 后回到外层模式。
- **风格不混用原则**：单个块内必须单一语法。`if x > 0 { }` 与 `if ($x -gt 0) { }` 不能在同一块内交替出现，否则报 `ParseError`。
- 块切换主要用于"导入已有 PS 脚本片段"场景，不鼓励在新建 `.osh` 文件中频繁混用。

#### 1.4 REPL 切换

- `#lang ps1`：REPL 切换到 PowerShell 兼容模式（影响后续输入行的解析）。
- `#lang osh`：REPL 切回现代模式（默认）。
- 切换指令本身在两种模式下都识别，作为元指令（meta-directive）。
- 切换不影响已定义的变量、函数、别名（这些是运行时状态，与语法无关）。

### 2. 操作符现代化

#### 2.1 操作符对照表

| 含义 | PowerShell | Modern (.osh) | 备注 |
|---|---|---|---|
| 相等 | `-eq` | `==` | |
| 不等 | `-ne` | `!=` | |
| 大于 | `-gt` | `>` | |
| 小于 | `-lt` | `<` | |
| 大于等于 | `-ge` | `>=` | |
| 小于等于 | `-le` | `<=` | |
| 与 | `-and` | `&&` | 短路 |
| 或 | `-or` | `\|\|` | 短路 |
| 非 | `-not` / `!` | `!` | |
| 匹配（通配符） | `-like` | `~=` | 简化 |
| 正则匹配 | `-match` | `~regex` | 简化 |
| 包含于 | `-in` | `in` | |
| 包含 | `-contains` | `contains` | 方法式 |
| 数组拼接 | `+` | `++` | 可选 |

#### 2.2 兼容与弃用

- 在 `.osh` 模式下，PowerShell 形式（`-eq` / `-ne` / `-gt` 等）仍可识别，但 parser emit `DeprecationWarning`，提示用户迁移到现代形式。
- 在 `.ps1` 模式下，现代形式（`==` / `!=` / `>` 等）**不识别**——PowerShell 兼容模式严格按 PS 语法解析，避免破坏已有脚本兼容性。
- `&&` / `||` 既是逻辑运算符（在 `if` 条件中短路求值），也是命令链运算符（per ADR-0045 §17，`cmd1 && cmd2` 语义）。

#### 2.3 字符串比较规则

- 字符串比较默认 `OrdinalIgnoreCase`（与 PowerShell 一致，per ADR-0042 §6 / ADR-0047 §6）。
- `-case` 开关切换大小写敏感：`$a == -case $b`（大小写敏感比较）。
- `-case` 是位置后缀修饰符，仅对紧邻的比较表达式生效。

### 3. 函数语法现代化

#### 3.1 函数定义对照

PowerShell:

```powershell
function Greet {
    param([string]$name = "World")
    "Hello, $name"
}
```

Modern (.osh):

```kotlin
fn greet(name: string = "World") -> string {
    "Hello, $name"
}
```

#### 3.2 关键字与签名

- **`fn` 关键字**：短，与 Kotlin `fun` 区分（避免完全照搬），与 Rust `fn` 一致。
- **函数名**：建议 snake_case（如 `greet` / `pipeline_cmd`），但 PascalCase 也合法（用于与 PowerShell cmdlet 命名一致的函数）。
- **参数**：`name: type` 形式，类型注解在后（与 Kotlin / Rust / TypeScript 一致），可选默认值 `= default`。
- **返回类型**：`-> type` 可省略，由函数体的 `return` 表达式或最后语句推导（per ADR-0047 §3 类型推导机制）。
- **类型注解支持**：`int` / `long` / `double` / `string` / `bool` / `array` / `hash` / `object` / `scriptblock` / `type`（per ADR-0047 §3.2 转换表对应类型）。

#### 3.3 箭头函数（lambda）

单表达式箭头函数：

```kotlin
square = x => x * 2
square(5)    # 10
```

等价于 PowerShell：

```powershell
$square = { param($x) $x * 2 }
& $square 5    # 10
```

多语句箭头函数：

```kotlin
process = x => {
    y = x + 1
    y * 2
}
```

- 箭头函数本质是 `ScriptBlock`（per ADR-0046 §2），编译到 `ScriptBlockAst`，与 `{ param($x) ... }` 完全等价。
- 单表达式 `x => expr`：体部是单条表达式语句，返回该表达式值。
- 多语句 `x => { stmt1; stmt2 }`：体部是脚本块，按 ADR-0046 §12 输出语义累计输出。
- 箭头函数支持闭包捕获（per ADR-0046 §7）。

#### 3.4 begin/process/end 块的 modern 形式

per ADR-0024（revised）+ ADR-0046 §6，函数体可包含 `begin` / `process` / `end` 三段：

```kotlin
fn pipeline_cmd {
    begin { sum = 0 }
    process { sum += $; $ }       # $ 是 $_ 的简写
    end { "total: $sum" }
}
```

- `begin` / `process` / `end` 是命名子块关键字，与 ADR-0046 §6 语义一致。
- `process` 块内 `$` 是当前管道对象（`$_` 的 modern 别名，见 §4）。
- 三个子块共享函数局部作用域（`begin` 设的 `sum` 在 `process` / `end` 可见）。

### 4. 字面量与访问语法

#### 4.1 字面量与访问对照表

| 含义 | PowerShell | Modern (.osh) | 备注 |
|---|---|---|---|
| 数组 | `@(1, 2, 3)` | `[1, 2, 3]` | 方括号数组（与 JSON 一致） |
| 哈希 | `@{ k = v; k2 = v2 }` | `{ k: v, k2: v2 }` | 花括号 + 冒号（与 JSON 一致） |
| 当前管道对象 | `$_` | `$` 或 `$.` | `$` 单独使用即当前对象 |
| 属性访问 | `$_.Name` | `$.name` 或 `$_.name` | 支持 PS 形式 |
| 方法调用 | `$obj.ToString()` | `$obj.to_string()` | snake_case 方法名（可选） |
| 索引 | `$arr[0]` / `$h["k"]` | `$arr[0]` / `$h["k"]` 或 `$h.k` | 哈希支持点访问 |
| Null 条件属性 | （无原生） | `?.` | `$.name?.ToUpper()` |
| Null 条件索引 | （无原生） | `?[]` | `$arr?[0]` |
| Null 合并 | `??`（PS 7+） | `??` | `$x ?? "default"` |
| 三元 | `? :`（PS 7+） | `? :` | `cond ? a : b` |
| 范围 | `1..10` | `1..10` | 保持一致 |
| 半开范围 | （无） | `0..<10` | 0 到 9（不含 10） |
| 字符串插值 | `"$x"` / `"$(expr)"` | `"$x"` / `"$(expr)"` 或 `"${expr}"` | 保留 `$(...)`，新增 `${...}` |
| Here-string | `@" ... "@` | `"""..."""` | 多行字符串（与 Kotlin 一致） |

#### 4.2 设计要点

- **数组 / 哈希字面量去掉 `@` 前缀**：更接近 JSON / JavaScript / Python，视觉更简洁。`[1, 2, 3]` 与 JSON 数组 `[1, 2, 3]` 视觉一致，`{ k: v }` 与 JSON 对象 `{"k": v}` 接近。
- **`$` 单独使用 = 当前管道对象**：`$_` 的简写，借鉴 Kotlin `it` 的简洁性。但保留 `$_` 兼容（per ADR-0042 §3.4）。
- **`$.property` = `$_.property` 的简写**：在 `process` 块 / `ForEach-Object` / `Where-Object` 等管道上下文内，`$.name` 比 `$_.name` 更短。
- **`?.` / `?[]` null 条件运算符**：PowerShell 无原生支持，借鉴 C# / Kotlin。`$.name?.ToUpper()` 当 `$.name` 为 null 时返回 null 而不抛错（per ADR-0047 §4.5 已有运行时支持）。
- **`??` null 合并**：PowerShell 7+ 已有，现代语法保留。
- **`? :` 三元**：PowerShell 7+ 已有，现代语法保留。
- **多行字符串 `"""..."""`**：替代 PowerShell here-string `@"..."@`，与 Kotlin / Python 一致，闭合标记无需在行首。

#### 4.3 数组与哈希字面量示例

PowerShell:

```powershell
$arr = @(1, 2, 3)
$hash = @{ Name = "Alice"; Age = 30; Nested = @{ City = "Shanghai" } }
```

Modern:

```kotlin
arr = [1, 2, 3]
hash = { name: "Alice", age: 30, nested: { city: "Shanghai" } }
```

- 数组仍是 `object[]`（per ADR-0047 §7.1），元素类型混合允许。
- 哈希仍是 `Hashtable`（大小写不敏感键，per ADR-0047 §6.1），键类型 `string`。
- 嵌套字面量递归构造。
- 空数组 `[]`、空哈希 `{}` 合法。

### 5. 控制流语法现代化

#### 5.1 控制流对照表

| 构造 | PowerShell | Modern (.osh) | 备注 |
|---|---|---|---|
| if | `if (cond) { }` | `if cond { }` | 不需括号 |
| elseif | `elseif (c) { }` | `elif c { }` | 与 Python 一致 |
| else | `else { }` | `else { }` | |
| while | `while (c) { }` | `while c { }` | |
| for（三段式） | `for ($i=0; $i -lt 10; $i++) { }` | `for i in 0..<10 { }` | Python 风格 for-in + range |
| foreach | `foreach ($x in $col) { }` | `for x in col { }` | 与 for 合并 |
| do-while | `do { } while (c)` | `do { } while c` | |
| do-until | `do { } until (c)` | `do { } until c` | |
| switch | `switch ($x) { }` | `match x { }` | Rust 风格 match |
| try | `try { } catch [Type] { } finally { }` | `try { } catch e: Type { } finally { }` | catch 绑定变量 |
| break | `break` / `break :Label` | `break` / `break label` | 去掉 `:` |
| continue | `continue` | `continue` | |
| return | `return $x` | `return x` | 去 `$`（表达式不需 `$` 前缀） |
| throw | `throw "msg"` | `throw "msg"` | |
| 管道链 | `cmd1 && cmd2` | `cmd1 && cmd2` | 保留 |

#### 5.2 match 示例

```rust
match color {
    "red"    => "stop"
    "yellow" => "caution"
    "green"  => "go"
    _        => "unknown"
}
```

- **`_` 表示 default**：与 Rust 一致（PowerShell 用 `default` 关键字）。
- **`=>` 分隔模式与结果**：单表达式，结果作为 case 输出。
- **多语句 case**：`"red" => { log(); "stop" }`，体部是脚本块（per ADR-0046）。
- **fall-through 语义**：现代模式建议默认**非 fall-through**（如 Rust），匹配一个 case 后自动退出。用 `|` 列多模式：`"red" | "crimson" => "stop"`。
- **`-regex` / `-wildcard` 开关**：`match -regex input { }` / `match -wildcard input { }`，与 PowerShell `switch -Regex` / `switch -Wildcard` 语义一致（per ADR-0045 §6）。
- **`break` 在 match 内**：由于默认非 fall-through，`break` 通常不需要；但显式 `break` 仍可提前退出整个 match（per ADR-0045 §8）。

#### 5.3 for-in 与 range

```kotlin
# 0 到 9（半开范围）
for i in 0..<10 {
    print(i)
}

# 1 到 10（闭范围）
for i in 1..10 {
    print(i)
}

# 迭代集合
for item in collection {
    print(item.name)
}

# 迭代哈希
for k, v in hash {
    print("$k = $v")
}
```

- `0..<10` 是半开范围（含 0 不含 10），借鉴 Rust / Swift。
- `1..10` 是闭范围（含两端），与 PowerShell 一致。
- `for x in col` 合并了 PowerShell 的 `foreach ($x in $col)`，关键字统一为 `for`。
- `for k, v in hash` 解构迭代哈希表（键值对），Python 风格。
- `break` / `continue` 语义同 ADR-0045 §8。

#### 5.4 try-catch-finally

```kotlin
try {
    get_content(path) -ErrorAction Stop
} catch e: System.IO.FileNotFoundException {
    "File not found: ${e.Exception.Message}"
} catch e: System.UnauthorizedAccessException {
    "Permission denied"
} catch e {
    "Other error: $e"
} finally {
    cleanup()
}
```

- `catch e: Type` 绑定异常到变量 `e`（现代语法显式绑定，比 PowerShell 的隐式 `$_` 更明确）。
- `catch e`（无类型）是兜底，捕获所有 `OpenShellException` 派生（per ADR-0045 §7）。
- `e` 是 `ErrorRecord`（per ADR-0026），与 `$_` 在 catch 块内是同一对象。
- `finally` 块至多一个，总是执行（per ADR-0045 §7）。
- `throw "msg"` 与 `throw $ErrorRecord` 语义不变（per ADR-0045 §7）。

### 6. 字符串与字面量

#### 6.1 字符串类型

| 语法 | 含义 | 插值 | 示例 |
|---|---|---|---|
| `'literal'` | 单引号字符串 | 否 | `'hello $name'` → `hello $name` |
| `"$x"` | 双引号字符串 | 是 | `"hello $name"` → `hello world` |
| `"""..."""` | 多行字符串 | 是 | 见下文 |
| `r"..."` | 原始字符串 | 否（且不转义反斜杠） | `r"C:\Users\name"` |

#### 6.2 多行字符串 `"""..."""`

```kotlin
multi = """
    Line 1
    Line 2 with $name interpolation
    Line 3 with ${expr} sub-expression
"""
```

- `"""` 开头，`"""` 闭合，与 Kotlin / Python 一致。
- 内部支持 `$var` / `${var}` / `$(...)` 插值（与双引号字符串规则一致，per ADR-0047 §8）。
- 内部换行保留为 `\n`（跨平台统一为 LF）。
- 闭合 `"""` 无需在行首（与 PowerShell here-string `@"..."@` 的行首要求不同）。
- 缩进处理：闭合 `"""` 的缩进决定公共前缀剥离（与 Kotlin 一致），避免多行字符串带多余缩进。

#### 6.3 原始字符串 `r"..."`

```kotlin
path = r"C:\Users\name\file.txt"
regex = r"\d+\.\d+"
```

- `r` 前缀表示原始字符串：不转义反斜杠 `\`，不插值 `$`。
- 借鉴 Rust `r"..."` / Python `r"..."`。
- 适用于 Windows 路径、正则表达式等含大量反斜杠的场景。

#### 6.4 字符串插值表达式

- `"$x"`：变量插值（per ADR-0047 §8.1）。
- `"$(expr)"`：子表达式插值（保留，与 PowerShell 一致）。
- `"${expr}"`：新增的子表达式插值形式，更明确边界（借鉴 Bash / Kotlin）。
  - `"${obj.property}"` 比 `"$(obj.property)"` 视觉更轻。
  - 嵌套场景：`"${outer.inner}"` 比 `"$(($outer).inner)"` 更清晰。

### 7. 类型注解

#### 7.1 类型注解对照

PowerShell:

```powershell
[int]$count = 0
[ValidateRange(0, 100)]
[int]$percent
```

Modern:

```kotlin
count: int = 0
percent: int @ValidateRange(0, 100)
```

#### 7.2 设计要点

- **类型在前缀 `[Type]` 改为后缀 `: type`**：与 Kotlin / Rust / TypeScript 一致，前缀 `[Type]` 形式保留用于 `.ps1` 兼容模式。
- **验证特性用 `@Attribute(args)` 语法**：借鉴 Python 装饰器，比 PowerShell 的 `[Attribute()]` 前缀更简洁。`@ValidateRange(0, 100)` 等价 `[ValidateRange(0, 100)]`。
- **变量声明不强制 `$`**：现代语法允许 `count = 0`（无 `$` 前缀），但保留 `$count = 0` 兼容形式。两种形式编译到同一 `VariableAssignmentAst`。
- **类型可省略**：`count = 0` 与 `count: int = 0` 等价（类型推导，per ADR-0047 §3）。
- **支持的类型**：`int` / `long` / `double` / `string` / `bool` / `array` / `hash` / `object` / `scriptblock` / `type`（per ADR-0047 §3.2 转换表）。
- **数组类型**：`nums: int[]` 或 `names: string[]`，与 PowerShell `[int[]]` / `[string[]]` 等价。

#### 7.3 函数参数类型注解

```kotlin
fn add(a: int, b: int) -> int {
    a + b
}

fn process(items: string[], opts: hash = {}) {
    for item in items {
        print(item)
    }
}
```

- 参数类型注解 `name: type` 形式，与函数返回类型 `-> type` 风格一致。
- 默认值 `= default` 表达式在每次调用时重新求值（per ADR-0046 §8）。
- 类型强制规则沿用 ADR-0047 §3 转换表。

### 8. 命令调用

#### 8.1 cmdlet 调用形式

- cmdlet 调用保持 `Verb-Noun` 形式（不变形），保持 PowerShell 兼容：`Get-ChildItem` / `Where-Object` / `ForEach-Object` 等。
- 参数 `-Name value` 保持 PowerShell 形式（避免破坏命令兼容性）：`get-item -path "C:/Users"`。
- 管道 `|` 保持：`get-childitem | where-object { $.length > 1MB }`。
- 别名系统（per ADR-0024）保留：`ls` / `gci` 等别名在 `.osh` 模式下可用。

#### 8.2 modern 简写（可选）

```kotlin
# PowerShell 形式
get-item -path "C:/Users"

# modern 关键字参数简写（可选）
get-item(path: "C:/Users")
```

- `cmd(name: value)` 是关键字参数风格，等价 `cmd -name value`。
- 仅作为可选简写，不强制使用；PowerShell 形式始终合法。
- 适用于参数较多时提升可读性，类似 Python `func(name=value)`。

#### 8.3 自动变量在命令调用中的可用性

- `$_` / `$args` / `$?` / `$LASTEXITCODE` / `$PSBoundParameters` / `$input` 等自动变量（per ADR-0042）在两种语法下都可用。
- `$` 是 `$_` 的 modern 别名（仅在管道上下文有效，per ADR-0042 §3.4）。
- `$?` / `$LASTEXITCODE` 在命令调用后自动更新（per ADR-0042 §3.1）。

### 9. 注释

#### 9.1 注释形式

| 语法 | 含义 | 示例 |
|---|---|---|
| `#` | 单行注释 | `# this is a comment` |
| `<# ... #>` | 多行注释 | `<# multi<br>line #>` |
| `"""..."""` | 文档注释（docstring） | 位于函数 / 类顶部 |
| `# TODO` | TODO 标记 | `# TODO: refactor this` |
| `# FIXME` | FIXME 标记 | `# FIXME: bug #123` |
| `# HACK` | HACK 标记 | `# HACK: workaround for X` |

#### 9.2 文档注释

```kotlin
"""
Greet a user by name.

Args:
    name: The user name, defaults to "World".

Returns:
    A greeting string.
"""
fn greet(name: string = "World") -> string {
    "Hello, $name"
}
```

- 文档注释用 `"""..."""`（与多行字符串同语法），位于函数 / 类顶部。
- 借鉴 Python docstring，工具链（LSP / 文档生成器）可解析。
- 内容建议用 Markdown 格式，便于渲染。
- 文档注释不产生运行时输出（与普通字符串表达式不同，parser 识别为 `DocumentationCommentAst`）。

### 10. 与 PS 互操作

#### 10.1 文件级互操作

- `import "script.ps1"`：显式以 PowerShell 兼容模式加载脚本。
- `import "module.osh"`：以 modern 模式加载模块。
- `import` 关键字是 modern 语法的模块加载指令（等价 PowerShell `. ./script.ps1` dot-source 或 `Import-Module`）。
- 加载的脚本中定义的函数、变量、别名按 ADR-0047 作用域规则注入当前作用域。

#### 10.2 块级互操作

```kotlin
# .osh 文件内嵌入 PS 块
#lang ps1 {
    function Legacy-Function {
        param([string]$input)
        $input | ForEach-Object { $_.ToUpper() }
    }
}

# 调用嵌入的 PS 函数（函数本身是 AST 节点，与语法无关）
Legacy-Function "hello"
```

- `#lang ps1 { ... }` 块内按 PowerShell 语法解析。
- 块内定义的函数、变量等运行时实体在块外可见（块切换仅影响语法解析，不影响作用域）。
- 反向 `#lang osh { ... }` 在 `.ps1` 文件内嵌入 modern 语法（对称支持）。

#### 10.3 自动变量互通

- `$_` / `$args` / `$?` / `$LASTEXITCODE` / `$PSBoundParameters` / `$input` / `$PSItem` 等自动变量（per ADR-0042）在两种语法下都可用。
- `$` 是 `$_` 的 modern 别名（仅在管道上下文有效，per ADR-0042 §3.4）。
- 现代语法 `$.name` 与 PowerShell `$_.name` 在运行时完全等价（同一 `MemberAccessAst`）。
- 现代语法定义的函数可被 PowerShell 模式调用，反之亦然（函数是 AST 节点，与语法无关）。

## Parser 实现方向

### 独立递归下降 parser

- 现代 parser（`ModernParser`）与 PowerShell parser（`PowerShellParser`）是**独立的**两个递归下降 parser。
- 现代 parser **不 fork** PowerShell parser，而是独立实现，避免维护 fork 分支的合并成本。
- 两个 parser 共享同一组 AST 节点类型（per ADR-0045 §14 + ADR-0046 §10），输出同一 AST。
- Evaluator 仅消费 AST 节点，不感知语法来源。

### AST 节点共享

现代语法新增的语法形式编译到已有 AST 节点，无需新增节点类型：

```csharp
// 现代语法 [1, 2, 3] 与 PowerShell @(1, 2, 3) 共享同一节点
public sealed record ArrayLiteralExpressionAst(
    IReadOnlyList<ExpressionAst> Elements) : ExpressionAst;

// 现代语法 { k: v } 与 PowerShell @{ k = v } 共享同一节点
public sealed record HashtableLiteralExpressionAst(
    IReadOnlyList<HashtableEntryAst> Entries) : ExpressionAst;

// 现代语法 fn greet(name: string) { } 与 PowerShell function greet { param([string]$name) } 共享同一节点
public sealed record FunctionDefinitionAst(
    string Name,
    ParameterBlockAst? Parameters,
    ScriptBlockAst Body) : StatementAst;

// 现代语法 match x { } 与 PowerShell switch ($x) { } 共享同一节点
// （match 编译为 SwitchStatementAst，Mode 字段标记非 fall-through）
public sealed record SwitchStatementAst(
    ExpressionAst Input,
    SwitchMode Mode,
    bool CaseSensitive,
    bool NonFallThrough,         // modern match 默认 true，PS switch 默认 false
    IReadOnlyList<SwitchCase> Cases,
    ScriptBlockAst? Default) : StatementAst;

// 现代语法 x => x * 2 与 PowerShell { param($x) $x * 2 } 共享 ScriptBlockAst
// （箭头函数是脚本块的语法糖，不新增节点）
```

### 风格选择机制

- 文件后缀决定默认 parser：`.osh` → `ModernParser`，`.ps1` → `PowerShellParser`。
- `#lang` 指令可在文件内切换 parser（块级），见 §1.3。
- 运行时无差异：两个 parser 输出的 AST 经同一 Evaluator 求值，行为完全一致。
- 工具链（LSP / formatter / debugger）基于 AST 工作，天然支持两种语法。

### Parser 错误信息

- 现代 parser 的错误信息标注 `[modern]` 前缀，PowerShell parser 标注 `[ps1]` 前缀，便于用户定位语法模式。
- 错误信息含源行号 / 列号 / 期望 token / 实际 token（per ADR-0045 §14）。
- 块切换错误：`#lang ps1 {` 未闭合 `}` 报 `UnclosedLangBlockError`。

## Examples

以下示例对比展示 PowerShell 与 Modern 两种写法，覆盖常见场景。

### 1. 函数定义 + 调用

PowerShell:

```powershell
function Add {
    param([int]$a, [int]$b = 1)
    return $a + $b
}
$result = Add 5 3
```

Modern:

```kotlin
fn add(a: int, b: int = 1) -> int {
    a + b
}
result = add(5, 3)
```

### 2. 管道 + ForEach-Object

PowerShell:

```powershell
Get-ChildItem -Path "C:/Users" |
    Where-Object { $_.Length -gt 1MB } |
    ForEach-Object { $_.Name } |
    Sort-Object -Descending
```

Modern:

```kotlin
get-childitem -path "C:/Users" |
    where-object { $.length > 1MB } |
    for-each-object { $.name } |
    sort-object -descending
```

### 3. 条件 + 循环

PowerShell:

```powershell
$sum = 0
foreach ($i in 1..10) {
    if ($i -gt 5) {
        $sum += $i
    } elseif ($i -eq 5) {
        Write-Host "Hit five"
    } else {
        # skip
    }
}
```

Modern:

```kotlin
sum = 0
for i in 1..10 {
    if i > 5 {
        sum += i
    } elif i == 5 {
        write-host "Hit five"
    } else {
        # skip
    }
}
```

### 4. 异常处理

PowerShell:

```powershell
try {
    Get-Content $path -ErrorAction Stop
} catch [System.IO.FileNotFoundException] {
    "File not found: $($_.Exception.Message)"
} catch {
    "Other error: $_"
} finally {
    Cleanup
}
```

Modern:

```kotlin
try {
    get-content path -ErrorAction Stop
} catch e: System.IO.FileNotFoundException {
    "File not found: ${e.Exception.Message}"
} catch e {
    "Other error: $e"
} finally {
    cleanup()
}
```

### 5. 数据处理（数组 / 哈希）

PowerShell:

```powershell
$users = @(
    @{ Name = "Alice"; Age = 30 },
    @{ Name = "Bob"; Age = 25 }
)
$names = @()
foreach ($u in $users) {
    $names += $u.Name
}
$oldest = $users | Sort-Object Age -Descending | Select-Object -First 1
```

Modern:

```kotlin
users = [
    { name: "Alice", age: 30 },
    { name: "Bob", age: 25 }
]
names = []
for u in users {
    names += u.name
}
oldest = users | sort-object age -descending | select-object -first 1
```

### 6. 完整脚本（综合）

PowerShell:

```powershell
function Scan-LargeFiles {
    param(
        [string]$root,
        [int]$minMB = 100
    )
    $threshold = $minMB * 1MB
    $results = @()
    try {
        foreach ($file in Get-ChildItem -Recurse $root) {
            try {
                if ($file.Length -gt $threshold) {
                    $results += $file
                }
            } catch [System.UnauthorizedAccessException] {
                continue
            }
        }
    } catch [System.UnauthorizedAccessException] {
        Write-Warning "Cannot access root: $root"
    } finally {
        $results | Sort-Object Length -Descending | Select-Object -First 10
    }
}
```

Modern:

```kotlin
fn scan_large_files(root: string, min_mb: int = 100) {
    threshold = min_mb * 1MB
    results = []
    try {
        for file in get-childitem -recurse root {
            try {
                if file.length > threshold {
                    results += file
                }
            } catch e: System.UnauthorizedAccessException {
                continue
            }
        }
    } catch e: System.UnauthorizedAccessException {
        write-warning "Cannot access root: $root"
    } finally {
        results | sort-object length -descending | select-object -first 10
    }
}
```

## Consequences

### 优势

- **现代语法更简洁**：`==` / `!=` / `>` / `<` 替代 `-eq` / `-ne` / `-gt` / `-lt`，`fn` / `name: type` 替代 `function` / `param([type]$name)`，`[1, 2, 3]` / `{ k: v }` 替代 `@(...)` / `@{ ... }`，书写负担显著降低。
- **更接近主流语言**：吸收 Python / Rust / Kotlin / C# 的现代语法元素，跨语言迁移成本降低。`==` / `!=` / `&&` / `||` / `!` / `?.` / `??` / `? :` 等运算符与 C 系语言肌肉记忆一致。
- **降低学习曲线**：新用户（尤其是已有 Python / JavaScript / Kotlin 经验的）学习 OpenShell 的门槛降低。现代语法是"PowerShell 语义 + 主流语言外观"的组合。
- **改善可读性**：去掉 `@` 前缀、`-` 前缀运算符、`param()` 块等噪音后，代码视觉更清爽，可读性提升。
- **同一 AST 复用工具链**：两种语法编译到同一 AST，调试器、LSP、formatter、coverage 工具无需区分语法来源，工具链投入一次即覆盖两种语法。
- **PowerShell 兼容不破坏**：`.ps1` 模式严格保留 PowerShell 语法，已有 PS 脚本零成本运行；`.osh` 模式作为现代语法的独立通道。

### 代价

- **双 parser 维护成本**：`ModernParser` 与 `PowerShellParser` 是两套独立实现，新增语法特性需同步更新两个 parser（除非特性仅在一种语法中提供）。预计现代 parser 约 3000 行 C# 代码，PowerShell parser 约 4000 行（含 legacy 兼容）。
- **用户可能混用导致风格分裂**：团队内若有人写 `.osh`、有人写 `.ps1`，代码库风格不一致。缓解：项目级约定默认语法（如 `.editorconfig` 配置 `openshell_default_syntax = osh`），CI 检查新文件后缀。
- **工具链需支持两套语法**：LSP 的语法高亮、formatter 的代码格式化、linter 的规则需同时支持两种语法。基于 AST 的工具（debugger / coverage）无影响，但基于源文本的工具（highlighter / formatter）需双份配置。
- **`match` 非 fall-through 与 PowerShell `switch` fall-through 语义差异**：两种语法的等价构造行为不同，用户切换语法时需注意。缓解：文档强调差异，`match` 默认非 fall-through，`switch` 默认 fall-through。
- **箭头函数与脚本块的等价关系**：`x => x * 2` 与 `{ param($x) $x * 2 }` 等价，但视觉差异大，初学者可能不理解两者互通。缓解：文档明确箭头函数是脚本块的语法糖。
- **`$` 简写的上下文限制**：`$` 仅在管道上下文有效（`process` 块 / `ForEach-Object` / `Where-Object` 等），在非管道上下文使用报 `ParseError`。需文档强调。

### 缓解策略

- **默认 modern 模式**：REPL 与新文件默认现代语法，文档优先 modern 示例，PS 模式仅作"导入已有脚本"通道。
- **文档双轨制**：每个语言特性的文档都提供 PS 与 modern 两种写法对比，便于用户对照学习。
- **弃用警告引导迁移**：`.osh` 模式下使用 PS 形式运算符（`-eq` 等）emit `DeprecationWarning`，引导用户逐步迁移。
- **LSP 自动转换**：未来提供"PS ↔ modern 语法转换"的 LSP 重构命令，一键转换源文本。
- **CI 检查**：项目级 `openshell_default_syntax` 配置 + CI 检查新文件后缀，避免风格分裂。

### 约束

- 文件后缀 `.osh` = 现代语法，`.ps1` = PowerShell 兼容；无后缀文件默认现代语法并 emit `ParseWarning`。
- REPL 默认现代语法，`#lang ps1` / `#lang osh` 切换。
- 单个块内必须单一语法，混用报 `ParseError`。
- 现代 parser 与 PowerShell parser 是独立实现，不 fork，共享 AST 节点类型。
- 现代 parser 输出的 AST 节点与 PowerShell parser 输出的节点类型完全一致（per ADR-0045 §14 + ADR-0046 §10）。
- `fn` / `match` / `elif` / `in` 是现代语法保留字，禁止作为命令名 / 别名 / 函数名 / 变量名。
- 现代运算符 `==` / `!=` / `>` / `<` / `>=` / `<=` / `&&` / `||` / `!` / `?.` / `??` / `? :` / `++` 是保留 token，不可重载。
- `~=` / `~regex` 是匹配运算符保留 token。
- `..`（闭范围）与 `..<`（半开范围）是范围运算符保留 token。
- `=>` 是箭头函数保留 token。
- `@` 前缀字面量（`@(...)` / `@{...}`）在 `.osh` 模式下 emit `DeprecationWarning`，但仍可解析（向后兼容）。
- PS 形式运算符（`-eq` / `-ne` / `-gt` 等）在 `.osh` 模式下 emit `DeprecationWarning`，但仍可解析。
- 现代形式运算符（`==` / `!=` 等）在 `.ps1` 模式下**不识别**，避免破坏 PS 兼容。
- `$` 单独使用仅在管道上下文（`process` 块 / `ForEach-Object` / `Where-Object` / `switch` 等）有效，非管道上下文报 `ParseError`。
- `$.property` 是 `$_.property` 的简写，仅在管道上下文有效。
- `match` 默认非 fall-through（与 `switch` 默认 fall-through 相反），需文档强调。
- `match` 的 `_` 表示 default（与 PowerShell `switch` 的 `default` 关键字不同）。
- `catch e: Type` 中 `e` 是 `ErrorRecord`（per ADR-0026），与 `$_` 在 catch 块内是同一对象。
- `for k, v in hash` 解构迭代仅适用于哈希表（`Hashtable` / `IDictionary`），其他类型报 `ParseError`。
- `r"..."` 原始字符串不插值、不转义反斜杠，但仍是 `string` 类型（与普通字符串运行时无差异）。
- `"""..."""` 多行字符串闭合 `"""` 无需在行首，缩进处理按 Kotlin 规则（闭合标记缩进决定公共前缀剥离）。
- `@Attribute(args)` 验证特性等价 `[Attribute(args)]`，编译到同一 `AttributeAst` 节点。
- `import "file.ps1"` / `import "file.osh"` 是模块加载指令，按文件后缀决定 parser。
- `#lang ps1 { ... }` / `#lang osh { ... }` 块切换必须闭合 `}`，未闭合报 `UnclosedLangBlockError`。
- 现代 parser 错误信息标注 `[modern]` 前缀，PowerShell parser 标注 `[ps1]` 前缀。
- 箭头函数 `x => expr` 编译到 `ScriptBlockAst`，与 `{ param($x) expr }` 完全等价（per ADR-0046）。
- 函数定义 `fn name(params) -> type { }` 编译到 `FunctionDefinitionAst`（per ADR-0024 revised），与 PS `function name { param() }` 等价。

## Alternatives Considered

1. **仅保留 PS 语法**：被否决（2026-07-08）。理由：用户已明确表态"原版 PowerShell script 写起来太丑了，不好看，然后也不够简洁"，要求"比 Python 更现代更好用更简洁"。仅保留 PS 语法无法满足用户对现代语法的需求，与 OpenShell 的"现代语法"目标冲突。

2. **仅 modern，放弃 PS 兼容**：被否决。理由：全兼容是 OpenShell 的核心目标之一（用户已确认"PowerShell 全兼容"方向）。放弃 PS 兼容意味着已有 PowerShell 脚本无法运行，PowerShell 生态（模块、脚本、社区资源）无法接入，OpenShell 将沦为"又一个新 shell"而非"PowerShell 的现代超集"。

3. **双语法（采纳）**：采纳。理由：双语法架构同时满足"PS 全兼容"与"现代语法"两个目标。`.ps1` 模式保留 PowerShell 兼容性，`.osh` 模式提供现代语法体验。两种语法编译到同一 AST，运行时语义完全一致，工具链基于 AST 复用。用户可按需选择语法，已有 PS 脚本零成本运行，新脚本用 modern 语法提升体验。

4. **在 PS 语法上做最小化糖**：被否决。理由：最小化糖（如仅把 `-eq` 改为 `==`）不足以满足"比 Python 更现代"的目标——`function` / `param()` / `@(...)` / `@{...}` / `@"..."@` 等噪音仍存在，整体观感仍像 PowerShell 而非现代语言。且最小化糖仍需独立 parser 处理新 token，与双 parser 成本相当，但收益不足。

5. **完全采用 Python 语法**：被否决。理由：Python 缩进敏感与 PowerShell `{ }` 块语义不兼容；Python 无管道 `|` 对象流；Python 的 `def` / `class` / `import` 语义与 shell 命令调用模型差异大。借鉴 Python 的简洁性（`for x in col` / `elif` / `match`）即可，无需完全照搬语法。

6. **完全采用 Rust 语法**：被否决。理由：Rust 的所有权 / 借用语义与 shell 动态类型模型冲突；Rust 的 `fn` / `match` / `->` 借鉴即可，无需引入 Rust 的类型系统复杂度。

7. **基于 TypeScript / JavaScript 语法**：被否决。理由：JS 的 `function` / `=>` 与 PowerShell 的 `function` 关键字冲突；JS 的 `let` / `const` 与 shell 变量模型差异大。借鉴 JS 的字面量语法（`[1, 2, 3]` / `{ k: v }`）即可。

## Open Questions

1. **`async` / `await` 支持**：是否在现代语法中引入 `async fn` / `await expr`？与 ADR-0010 异步管道的交互如何设计？现代语法已支持 `IAsyncEnumerable<IItem>` 管道（per ADR-0010），但用户级 `async` / `await` 语法尚未定义。需评估是否在 M5+ 引入。

2. **类型系统深度**：现代语法的类型注解目前支持基础类型（`int` / `string` / `bool` / `array` / `hash` 等）。是否引入 union type（`int | string`）、generic（`List<int>`）、optional type（`int?`）？深度类型系统会显著增加 parser 与运行时复杂度，需独立 ADR 决策。

3. **模式匹配（pattern matching）**：当前 `match` 仅支持字面量模式（`"red" => ...`）与类型模式（`catch e: Type`）。是否引入更强大的模式匹配，如解构模式（`{ name, age } => ...`）、范围模式（`1..=10 => ...`）、守卫模式（`x if x > 0 => ...`）？这超出 switch / match 的基础范围，需独立 ADR。

4. **macro / 元编程支持**：是否在现代语法中引入宏（macro）或编译时元编程？如 Rust `macro_rules!` / Lisp 宏。这会显著改变语言性质，从"shell 脚本"走向"系统编程语言"，需谨慎评估。

5. **`for k, v in hash` 的解构推广**：当前仅哈希迭代支持解构。是否推广到任意可解构对象（如 `for { name, age } in users`）？需评估与 PowerShell `PSCustomObject` 的交互。

6. **`import` 关键字与模块系统**：`import "file.osh"` 是简单的文件加载。是否引入完整模块系统（如 `import { fn1, fn2 } from "module"` / `export fn ...`）？需评估与 PowerShell 模块系统（`*.psm1` / `Import-Module`）的兼容性。

7. **运算符重载**：现代语法是否允许用户自定义运算符重载（如 `==` 对自定义类型的语义）？PowerShell 不支持运算符重载（仅方法），引入重载会与 PS 兼容性冲突。需评估。

8. **`$` 简写的扩展**：`$` 当前仅作为 `$_` 的简写。是否扩展到其他场景（如 `$1` / `$2` 表示管道历史项，借鉴 bash）？需评估与 PowerShell `$_` 语义的兼容性。

## References

- PowerShell about_Operators: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_operators
- PowerShell about_Functions: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_functions
- PowerShell about_Hash_Tables: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_hash_tables
- PowerShell about_Arrays: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_arrays
- PowerShell about_Quoting_Rules: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_quoting_rules
- Kotlin Syntax Reference: https://kotlinlang.org/docs/reference/syntax.html
- Rust match Expression: https://doc.rust-lang.org/reference/expressions/match-expr.html
- Python For Statements: https://docs.python.org/3/reference/compound_stmts.html#the-for-statement
- ADR-0008 CLI REPL Architecture
- ADR-0010 Pipeline Object Stream
- ADR-0024 Aliases and Functions (revised)
- ADR-0042 Automatic Variables (revised)
- ADR-0045 Control Flow
- ADR-0046 Script Blocks
- ADR-0047 Variable System
- ADR-0048 Cmdlets
