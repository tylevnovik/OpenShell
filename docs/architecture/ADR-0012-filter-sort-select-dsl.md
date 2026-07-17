# ADR-0012: Filter / Sort / Select DSL

- **Status**: Accepted
  - Revised 2026-07-08: switched from property-name DSL to PowerShell-style script blocks per user direction (PowerShell 全兼容). Previous design (property-name DSL) kept as a shortcut form.
- **Date**: 2026-07-07 (revised 2026-07-08)
- **Stage**: M2
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0010 (Pipeline), ADR-0011 (Formatting), ADR-0003 (Item 模型), ADR-0036 (Security Sandbox), ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0047 (Variable System), ADR-0048 (Cmdlets)

## Context

M2 的 Pipeline 节点 `Where-Object` / `Select-Object` / `Sort-Object` / `ForEach-Object` 需要表达式语法。最初（2026-07-07）本 ADR 选择"自研属性名 DSL"路线，明确拒绝 PowerShell 脚本块，理由是安全（不允许任意代码执行）。用户在 2026-07-08 决定走 "PowerShell 全兼容" 路线，因此本 ADR 修订为：**以 PowerShell 风格脚本块为主**，原属性名 DSL 作为语法糖保留。

修订后的预期用法（两种形式并列）：

```powershell
# 快捷 DSL 形式（语法糖）
get-childitem | where size > 1MB -and name ~= "*.txt"
get-childitem | select name, size
get-childitem | sort size desc
get-childitem | foreach name.ToUpper()

# PowerShell 脚本块形式（主）
get-childitem | Where-Object { $_.Size -gt 1MB -and $_.Name -like "*.txt" }
get-childitem | Select-Object -Property Name, Size
get-childitem | Sort-Object { $_.Size } -Descending
get-childitem | ForEach-Object { $_.Name.ToUpper() }
```

需求：

1. **PowerShell 全兼容**：`Where-Object { ... }` / `Sort-Object { ... }` / `Select-Object -Property ...` / `ForEach-Object { ... }` 必须可被 PowerShell 用户零成本迁移
2. **`$_` / `$PSItem`**：脚本块内可用 `$_` 或 `$PSItem` 表示当前管道项
3. **属性访问**：`$_.Size` 直接访问 `IItem` 属性；快捷形式 `size` 由 Parser lowering 为 `$_.Size`
4. **类型感知**：`size` 是 long，比较用数值；`name` 是 string，支持 `-like` glob；`modified` 是 DateTimeOffset
5. **字面量**：数字（含 KB/MB/GB 单位）、字符串、时间、bool、null
6. **运算符（双形式）**：
   - PowerShell 风格：`-eq -ne -gt -lt -ge -le -match -notmatch -like -notlike -in -notIn -contains -notContains`
   - 快捷形式：`= != < > <= >= ~= in contains`（由 Parser lowering 到对应 `-eq/-ne/-gt/...`）
7. **逻辑运算符（双形式）**：PowerShell `-and -or -not -xor` 与 C 风格 `&& || !` 并存
8. **投影表达式**：`Select-Object -Property Name, @{Name="SizeMB"; Expression={$_.Size/1MB}}` 支持计算列
9. **多键排序**：`Sort-Object -Property @{Expression={$_.Size}; Descending=$true}, @{Expression={$_.Name}; Ascending=$true}`
10. **任意变换**：`ForEach-Object { $_.Name.ToUpper() }`（原 ADR 明确拒绝，现已放开）
11. **解析容错**：表达式错误时报错信息含位置（哪个 token）
12. **安全模型转移**：脚本块允许任意计算，安全责任由 DSL 层下沉到命令级沙箱（ADR-0036）

参考选项：

- PowerShell `{ $_.Size -gt 1MB }` 脚本块：**采纳**（主形式），需要脚本引擎（ADR-0046）
- Nushell `where size > 1mb` 纯表达式：保留为快捷形式
- 嵌入 Lua / JavaScript：被否决，依赖外部运行时
- Roslyn C# 表达式：被否决，编译慢、安全沙箱复杂
- 自研小 AST：保留为快捷形式的内部实现

## Decision

### 1. 双形式架构

**主机制 — PowerShell 风格脚本块 `{ }`**：

- `Where-Object { <predicate> }` 接受任意脚本块作为谓词
- `ForEach-Object { <transform> }` 接受任意脚本块作为变换
- `Sort-Object { <key> }` 接受脚本块作为排序键
- `Select-Object -Property <list>` 接受属性列表或 `@{Name=...; Expression={...}}` 计算列
- 脚本块内 `$_` / `$PSItem` 绑定到当前管道 `IItem`（ADR-0010）
- 脚本块编译/求值由 ADR-0046（Script Blocks）定义的引擎承担

**快捷形式 — 属性名 DSL（语法糖）**：

- `where size > 1MB` 由 Parser lowering 为 `Where-Object { $_.Size -gt 1MB }`
- `select name, size` 由 Parser lowering 为 `Select-Object -Property Name, Size`
- `sort size desc` 由 Parser lowering 为 `Sort-Object { $_.Size } -Descending`
- `foreach name.ToUpper()` 由 Parser lowering 为 `ForEach-Object { $_.Name.ToUpper() }`
- 快捷形式经 lowering 后与脚本块形式共享同一求值路径，无运行时差异

并列表达示例：

```powershell
# 快捷 DSL 形式
get-childitem | where size > 1MB
get-childitem | where size > 1MB -and name ~= "*.txt"
get-childitem | select name, size
get-childitem | sort size desc, name asc
get-childitem | foreach name.ToUpper()

# PowerShell 脚本块形式（等价）
get-childitem | Where-Object { $_.Size -gt 1MB }
get-childitem | Where-Object { $_.Size -gt 1MB -and $_.Name -like "*.txt" }
get-childitem | Select-Object -Property Name, Size
get-childitem | Sort-Object { $_.Size } -Descending, { $_.Name } -Ascending
get-childitem | ForEach-Object { $_.Name.ToUpper() }
```

### 2. 快捷 DSL Grammar（Shortcut form）

快捷形式仍是自研轻量 AST + Parser，但其 AST 在求值前先 lowering 到脚本块 AST。Grammar 保留如下：

```
expr      := orExpr
orExpr    := andExpr ( OR andExpr )*
andExpr   := notExpr ( AND notExpr )*
notExpr   := NOT notExpr | comparison
comparison:= primary ( OP primary )?
primary   := property | literal | '(' expr ')'
property  := IDENT ( '.' IDENT )*
literal   := NUMBER [UNIT] | STRING | TRUE | FALSE | 'null' | DATE
sortSpec  := expr (ASC|DESC) (',' expr (ASC|DESC))*
selectSpec:= projection (',' projection)*
projection:= expr ('as' IDENT)?
foreachSpec:= methodCall | expr
```

运算符优先级（快捷形式）：`-not` / `!` > 比较运算符 > `-and` / `&&` > `-or` / `||`。

**Lowering 映射表**：

| 快捷形式 | 脚本块形式 | 说明 |
|---|---|---|
| `where size > 1MB` | `Where-Object { $_.Size -gt 1MB }` | 比较 |
| `where name ~= "*.txt"` | `Where-Object { $_.Name -like "*.txt" }` | glob |
| `where size > 1MB -and name ~= "*.txt"` | `Where-Object { $_.Size -gt 1MB -and $_.Name -like "*.txt" }` | 逻辑组合 |
| `select name, size` | `Select-Object -Property Name, Size` | 投影 |
| `select name, ${size/1MB} as sizeMB` | `Select-Object -Property Name, @{Name="SizeMB"; Expression={$_.Size/1MB}}` | 计算列 |
| `sort size desc` | `Sort-Object { $_.Size } -Descending` | 单键排序 |
| `sort size desc, name asc` | `Sort-Object @{Expression={$_.Size}; Descending=$true}, @{Expression={$_.Name}; Ascending=$true}` | 多键排序 |
| `foreach name.ToUpper()` | `ForEach-Object { $_.Name.ToUpper() }` | 变换 |

### 3. Comparison Operators（双形式）

PowerShell 风格（脚本块内主用）：

| 运算符 | 含义 | 示例 |
|---|---|---|
| `-eq` | 等于 | `Where-Object { $_.Size -eq 100 }` |
| `-ne` | 不等于 | `Where-Object { $_.Name -ne "foo" }` |
| `-gt` | 大于 | `Where-Object { $_.Size -gt 1MB }` |
| `-lt` | 小于 | `Where-Object { $_.Size -lt 1MB }` |
| `-ge` | 大于等于 | `Where-Object { $_.Size -ge 1MB }` |
| `-le` | 小于等于 | `Where-Object { $_.Size -le 1MB }` |
| `-match` | 正则匹配 | `Where-Object { $_.Name -match "^foo" }` |
| `-notmatch` | 正则不匹配 | `Where-Object { $_.Name -notmatch "^foo" }` |
| `-like` | glob 匹配 | `Where-Object { $_.Name -like "*.txt" }` |
| `-notlike` | glob 不匹配 | `Where-Object { $_.Name -notlike "*.tmp" }` |
| `-in` | 集合包含 | `Where-Object { $_.Ext -in @("txt","md") }` |
| `-notIn` | 集合不包含 | `Where-Object { $_.Ext -notIn @("tmp","log") }` |
| `-contains` | 集合包含元素 | `Where-Object { $_.Tags -contains "prod" }` |
| `-notContains` | 集合不包含元素 | `Where-Object { $_.Tags -notContains "dev" }` |

快捷形式（DSL 内语法糖，lowering 到上表）：

| 快捷 | Lowering 到 |
|---|---|
| `=` | `-eq` |
| `!=` | `-ne` |
| `<` | `-lt` |
| `>` | `-gt` |
| `<=` | `-le` |
| `>=` | `-ge` |
| `~=` | `-like` |
| `!~=` | `-notlike` |
| `in` | `-in` |
| `contains` | `-contains` |

### 4. Logical Operators（双形式）

| PowerShell 风格 | C 风格（快捷） | 含义 |
|---|---|---|
| `-and` | `&&` | 逻辑与 |
| `-or` | `\|\|` | 逻辑或 |
| `-not` | `!` | 逻辑非 |
| `-xor` | — | 逻辑异或（仅脚本块形式） |

两形式在脚本块内均可使用（`{ $_.A -gt 1 -and $_.B -lt 10 }` 与 `{ $_.A -gt 1 && $_.B -lt 10 }` 等价）。

### 5. Sort DSL（双形式）

```powershell
# 快捷形式
get-childitem | sort size desc
get-childitem | sort size desc, name asc

# 脚本块形式
get-childitem | Sort-Object { $_.Size } -Descending
get-childitem | Sort-Object @{Expression={$_.Size}; Descending=$true}, @{Expression={$_.Name}; Ascending=$true}

# 计算键
get-childitem | Sort-Object { $_.Name.Length } -Descending
```

`sort` 是 buffering transform，全量收集后排序再输出（ADR-0010 §6）。`--top N` 参数提前终止上游。

### 6. Select DSL（双形式）

```powershell
# 快捷形式
get-childitem | select name, size
get-childitem | select name, ${size/1MB} as sizeMB

# 脚本块形式
get-childitem | Select-Object -Property Name, Size
get-childitem | Select-Object -Property Name, @{Name="SizeMB"; Expression={$_.Size/1MB}}
get-childitem | Select-Object -Property Name, @{Name="IsLarge"; Expression={$_.Size -gt 1MB}}
```

每元素输出一个新 `Item`（用 ADR-0003 的 `with` 派生）。

### 7. ForEach-Object（新增）

原 ADR 拒绝脚本块，因此不提供 `ForEach-Object`。修订后新增：

```powershell
# 快捷形式
get-childitem | foreach name.ToUpper()
get-childitem | foreach "$($_.Name) - $($_.Size)"

# 脚本块形式
get-childitem | ForEach-Object { $_.Name.ToUpper() }
get-childitem | ForEach-Object { "$($_.Name) - $($_.Size)" }
get-childitem | ForEach-Object { [PSCustomObject]@{ Name=$_.Name; SizeMB=[math]::Round($_.Size/1MB,2) } }
```

`ForEach-Object` 是 `IPipelineTransform`，对每个输入 `IItem` 应用脚本块，输出结果（可能是变换后的 `IItem`、字符串、或新对象）。`begin` / `process` / `end` 三段脚本块（PowerShell 语法）由 ADR-0046 定义，本 ADR 仅消费 `process` 段。

### 8. 脚本块求值（依赖 ADR-0046）

脚本块 `{ ... }` 的解析、绑定、求值由 ADR-0046（Script Blocks）定义的引擎承担。本 ADR 仅规定：

- `$_` / `$PSItem` 在 `Where-Object` / `ForEach-Object` / `Sort-Object` 谓词内绑定到当前 `IItem`
- 脚本块在管道内编译一次，每元素复用（同快捷 AST 的缓存策略）
- 脚本块内可访问外层作用域变量（ADR-0047 Variable System）
- 脚本块内可调用 cmdlet（ADR-0048），受 ADR-0036 沙箱约束

```csharp
[Verb("Where", Noun = "Object", Group = CommandGroup.Pipeline, PipelineOnly = true)]
public sealed class WhereObjectCommand : IPipelineTransform
{
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock Predicate);

    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input, CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var bound = Args.Predicate.Bind(ctx);    // 编译一次
        await foreach (var item in input.WithCancellation(ct))
        {
            using (ctx.PushScope(item))          // $_ = item
            {
                if (bound.ToBoolean())
                    yield return item;
            }
        }
    }
}
```

### 9. 快捷形式 AST（Lowering 内部表示）

快捷形式仍是自研 AST，但 `ExprEvaluator` 不再独立求值，而是先 lowering 到 `ScriptBlockAst`，再走脚本块引擎。AST 节点保留：

```csharp
public abstract record ExprAst;

public sealed record ComparisonExpr(
    PropertyAccessExpr Left,
    ComparisonOp Op,
    LiteralExpr Right) : ExprAst;

public sealed record LogicalExpr(
    ExprAst Left,
    LogicalOp Op,
    ExprAst Right) : ExprAst;

public sealed record NotExpr(ExprAst Inner) : ExprAst;

public sealed record PropertyAccessExpr(
    string Name,
    ExprAst? SubExpression = null)
    : ExprAst;

public sealed record LiteralExpr(object? Value, LiteralKind Kind) : ExprAst;

public sealed record ProjectionExpr(
    ExprAst Expression,
    string? Alias = null) : ExprAst;

public enum ComparisonOp { Eq, Ne, Lt, Gt, Le, Ge, Glob, NotGlob, Match, NotMatch, In, NotIn, Contains, NotContains }
public enum LogicalOp { And, Or, Xor }
public enum LiteralKind { Number, String, Boolean, Date, Duration, Null }

public static class DslLowering
{
    public static ScriptBlockAst ToScriptBlock(ExprAst expr) { /* ... */ }
}
```

### 10. 字面量支持

- 数字：`123`, `1.5`, `0x1F`, `0b101`
- 数字 + 单位：`1KB` `2MB` `3GB` `4TB`（大小写不敏感，1024 进制）
- 字符串：`"..."`（支持 `\n \t \"`、`$($_.X)` 插值）、`'...'`（原样）
- 日期：`2026-01-01`, `2026-01-01T12:00:00Z`, `"2026-01-01 12:00"`
- 持续时间：`1d`, `2h`, `30m`, `15s`（用于 `where modified > now - 7d`）
- bool：`$true` / `$false`（脚本块内）/ `true` / `false`（快捷形式内）
- null：`$null` / `null`

### 11. 属性访问规则

脚本块形式：`$_.Size` → `item.Size`；`$_.Name` → `item.Name`；`$_.Modified` → `item.Timestamps.Modified`；`$_.Path` → `item.Path.Display`；`$_.Attributes` → `item.Properties["attributes"]`；`$_.foo` → Properties 字典查找。

快捷形式：`size` / `name` / `modified` / `path` / `attributes` / `*.foo` 由 lowering 注入 `$_.` 前缀。

未找到属性时返回 `$null`，比较运算符对 null 有特殊规则（`$null -gt 1MB` = `$false`，`$null -eq $null` = `$true`）。

### 12. glob 与正则

- `-like` / `~=` → `System.IO.Enumeration.FileSystemName.MatchesSimpleExpression`
- `-notlike` / `!~=` → 取反
- `-match` → `.NET Regex.IsMatch`（默认大小写不敏感，`-cmatch` 大小写敏感）
- `-notmatch` → 取反

### 13. 错误信息

Parser 错误（快捷形式）：

```
[parse error] where size >> 1MB
                     ^ expected comparison operator, got '>>'
```

脚本块编译错误（脚本块形式）：

```
[compile error] Where-Object { $_.Size -gtt 1MB }
                                  ^ unknown operator '-gtt'
```

求值错误（如类型不兼容）：

```
[eval error] where size > "abc"
                     ^ cannot compare long with string
```

错误不抛异常到管道层，由 `where` / `foreach` 命令捕获后转为 warning + 跳过该元素（除非 `--strict`）。

### 14. select / sort 实现

`Select-Object` 命令的 Args 是 ProjectionExpr 列表（快捷）或 `ScriptBlock` + 计算列（脚本块），每元素输出一个新 `Item`：

```csharp
[Verb("Select", Noun = "Object", Group = CommandGroup.Pipeline, PipelineOnly = true)]
public sealed class SelectObjectCommand : IPipelineTransform
{
    public async IAsyncEnumerable<IItem> Transform(...)
    {
        var specs = ParseSpecs(Args);   // 同时支持快捷与 -Property
        await foreach (var item in input.WithCancellation(ct))
        {
            var newItem = item;
            foreach (var spec in specs)
            {
                using (ctx.PushScope(item))
                {
                    var value = spec.Evaluate(ctx);
                    newItem = newItem with { Properties = newItem.Properties.With(spec.Alias ?? spec.Name, value) };
                }
            }
            yield return newItem;
        }
    }
}
```

`Sort-Object` 是 buffering transform，全量收集后排序再输出。

## Alternatives Considered

1. **拒绝脚本块，仅用属性名 DSL（原 ADR-0012 设计）**：被否决（2026-07-08）。理由：阻断 PowerShell 全兼容目标，无法迁移现有 PS 脚本；`ForEach-Object` 无法实现；用户被迫学习新 DSL。原设计的"安全"优势可通过命令级沙箱（ADR-0036）替代获得。
2. **Roslyn C# 表达式**：被否决，编译开销大（>100ms），沙箱复杂
3. **嵌入 Lua（NLua）**：被否决，引入外部运行时 + GC 桥接
4. **JINT（JavaScript）**：被否决，语义与 .NET 类型系统不对齐
5. **LINQ 表达式字符串（Dynamic LINQ）**：被否决，依然有注入风险，且不支持 glob/日期字面量
6. **完全不支持 DSL，让用户写 `--predicate` C# 函数**：被否决，用户体验差，开发门槛高
7. **仅脚本块，删除快捷形式**：被否决，快捷形式对简单查询（`where size > 1MB`）体验显著优于 `Where-Object { $_.Size -gt 1MB }`，且 GUI 可视化筛选器（M3）可基于快捷 AST 生成。两形式共存是 PowerShell 兼容 + 易用性的最佳平衡。

## Consequences

### 优势

- **PowerShell 全兼容**：现有 PS 脚本（`Where-Object { ... }` / `ForEach-Object { ... }`）可直接迁移
- **表达力强**：脚本块内可调用方法、拼接字符串、构造对象，无原 DSL 的能力上限
- **`ForEach-Object` 可用**：原 ADR 拒绝，现放开，支持任意变换
- **双形式平衡**：快捷形式给简单场景以简洁语法，脚本块给复杂场景以完整能力
- **类型感知**：数字单位、日期、glob 一等支持（两形式共享）
- **错误信息含位置**
- **GUI 可视化筛选器**（M3）可基于快捷 AST 生成，再 lowering 到脚本块执行
- **`where` / `select` / `sort` / `foreach` 共用脚本块引擎**（ADR-0046）

### 代价

- **安全模型下沉**：脚本块允许任意计算，安全责任由 DSL 层转移到命令级沙箱（ADR-0036）。沙箱必须拦截文件 IO、网络、进程启动、反射等危险操作。
- **依赖脚本块引擎（ADR-0046）**：M2 必须先交付脚本块基础，`Where-Object`/`ForEach-Object` 才能工作
- **依赖变量系统（ADR-0047）**：`$_` / `$PSItem` 作用域绑定需要变量系统支持
- **快捷形式 lowering 增加复杂度**：Parser 需同时支持两形式并统一到脚本块 AST
- **新增运算符需改 Lexer + Parser + Lowering + 脚本块引擎**
- **快捷 AST 代码量**：约 500 行 + lowering 约 200 行

### 约束

- 脚本块求值器不得修改输入 `IItem`（除非是 `ForEach-Object` 显式输出新对象）
- 脚本块内 cmdlet 调用受 ADR-0036 沙箱约束，禁止逃逸操作（文件写、网络、进程）
- 快捷形式经 lowering 后与脚本块形式**语义等价**，禁止快捷形式有脚本块无法表达的行为
- 未识别的属性返回 `$null`，不抛异常
- 类型不兼容的比较返回 `$false`（不抛异常），便于容错
- `--strict` 模式下错误转为异常，短路管道
- 表达式缓存：同一脚本块/快捷 AST 在管道内只编译一次
- Lexer 必须支持 Unicode 标识符（中文属性名等）
- 数字单位 `KB/MB/GB` 是 1024 进制，文档明确说明（非 1000）
- `$_` 与 `$PSItem` 必须可互换
- 快捷形式与脚本块形式不得在同一表达式内混用（如 `where size > 1MB -and { $_.Ext -eq "txt" }`），需选择其一
