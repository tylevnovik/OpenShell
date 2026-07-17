# ADR-0046: 脚本块（Script Blocks）作为一等值

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0010 (Pipeline), ADR-0012 (Filter DSL, revised), ADR-0024 (Functions, revised), ADR-0042 (Variables, revised), ADR-0045 (Control Flow), ADR-0047 (Variable System), ADR-0048 (Cmdlets)

## Context

PowerShell 把 `{ ... }` 视为一等值：脚本块（`[scriptblock]`）既能存储在变量里、又能作为参数传递、还能在任意位置延迟求值。这是 PowerShell 语言的核心机制，支撑了 `Where-Object { ... }` / `ForEach-Object { ... }` / `Sort-Object -Property { ... }` / `Invoke-Command { ... }` 等场景，也是用户自定义函数与 `&` 调用运算符的基础。

OpenShell 当前（M2 结束时）完全没有脚本块概念：

- ADR-0010 的管道模型只能传 `IItem`，节点之间没有"代码值"载体
- ADR-0024 的函数体只是 TOML 字符串，不是可调用的脚本块（首版明确拒绝 PowerShell 完整 `function` 语义）
- ADR-0042 的变量系统只支持 `string / int / long / bool / ItemPath / IItem` 等基础类型，没有 `[scriptblock]` 类型
- ADR-0012 修订后明确依赖 ADR-0046 提供 `{ ... }` 解析与求值引擎（见 ADR-0012 §8）

用户在 2026-07-08 决定走 "PowerShell 全兼容" 路线，本 ADR 是该路线的核心语言层：把 `{ ... }` 定义为运行时一等值，定义其类型、调用方式、参数绑定、与管道 / 函数 / 变量系统的集成。

### 痛点

1. **无脚本块载体**：`Where-Object { ... }` 等命令无法把 `{ }` 当作值接收
2. **函数体表达力弱**：ADR-0024 首版函数体不支持控制流、不支持 `param()`、不支持 `begin/process/end`
3. **闭包缺失**：无法实现 `Get-Counter` / `Get-Block` 等返回脚本块的惯用法
4. **管道与代码耦合**：脚本块无法跨管道节点复用，每节点需独立编译
5. **IDE 与调试盲区**：无 AST 反射，调试器无法在脚本块内设断点，IDE 无法语法高亮
6. **与 PowerShell 不兼容**：现有 PS 脚本（`Invoke-Command { ... }` / `$sb = { ... }; & $sb`）无法迁移

### 依赖关系

- **下游依赖**（本 ADR 被以下 ADR 依赖）：
  - ADR-0012 修订版：`Where-Object` / `ForEach-Object` / `Sort-Object` 的脚本块参数由本 ADR 提供引擎
  - ADR-0024 修订版：`function` 关键字定义的函数体本质是脚本块
  - ADR-0048（Cmdlets）：cmdlet 的 `[scriptblock]` 形参依赖本 ADR 的类型与求值器
- **上游依赖**（本 ADR 依赖以下 ADR）：
  - ADR-0010：管道对象流，脚本块的 `process` 段消费 `IAsyncEnumerable<IItem>`
  - ADR-0045（Control Flow）：脚本块体内允许 `if / for / while / switch / try`
  - ADR-0047（Variable System）：`$_` / `$PSItem` / `$_.Property` 绑定、闭包作用域查找

### 设计原则

1. **PowerShell 全兼容**：脚本块语法、`&` 调用运算符、`begin/process/end` 三段、`param()` 块、闭包语义对齐 PowerShell 5.1+
2. **一等值**：脚本块可作为变量值、参数、返回值、数组 / 哈希表元素
3. **AST 可反射**：保留源文本，调试器与 IDE 可获取 AST 节点
4. **安全责任下沉**：脚本块允许任意计算，安全责任由命令级沙箱（ADR-0036）承担，本 ADR 不重复安全机制
5. **不引入新容器类型**：复用 ADR-0010 的 `IItem` 与 ADR-0042 的变量作用域

## Decision

### 1. 脚本块语法

```powershell
# 简单脚本块
$sb = { $_.Name.ToUpper() }

# 带 param() 块
$sb = {
    param([string]$Prefix)
    "$Prefix$($_.Name)"
}

# 多语句、控制流（per ADR-0045）
$sb = {
    $sum = 0
    foreach ($n in 1..10) { $sum += $n }
    $sum
}

# begin/process/end 三段（见 §6）
$sb = {
    begin { $count = 0 }
    process { $count++; $_.Name }
    end { Write-Verbose "Processed $count items" }
}

# 空脚本块合法
$empty = { }

# 内联脚本块（无需赋值，直接被 cmdlet 接收）
Get-ChildItem | Where-Object { $_.Length -gt 1MB }
```

语法规则：

- `{` 与 `}` 是分隔符 token（reserved，见"约束"小节）
- 可选 `param()` 块作为第一条语句，若存在必须位于最前
- 体部是零个或多个语句，允许控制流（ADR-0045）
- 多行脚本块：首尾空白按 PowerShell 惯例忽略（前导换行与缩进不参与求值）
- 空脚本块 `{ }` 合法：求值返回 `$null`，无输出
- 单行脚本块 `{ "hello" }` 合法：等价于多行形式
- 脚本块内可嵌套脚本块：`{ { $_.Name } }` 是合法的外层脚本块，其体部为一个内层脚本块表达式

### 2. 脚本块类型

定义 sealed 类 `ScriptBlock`，位于 `OpenShell.Runtime` 命名空间（完整类型名 `OpenShell.Runtime.ScriptBlock`）：

```csharp
namespace OpenShell.Runtime;

/// <summary>
/// 一等脚本块值，对应 PowerShell 的 [scriptblock]。
/// Per ADR-0046.
/// </summary>
public sealed class ScriptBlock
{
    public ScriptBlock(ScriptBlockExpression ast, ExecutionContext ctx)
    {
        Ast = ast;
        CapturedContext = ctx;
    }

    /// <summary>AST 节点。Per ADR-0046 §10.</summary>
    public ScriptBlockExpression Ast { get; }

    /// <summary>捕获的执行上下文（变量作用域、命令注册表等）。Per ADR-0046 §4 闭包语义。</summary>
    public ExecutionContext CapturedContext { get; }

    /// <summary>是否有命名块（begin/process/end）。Per ADR-0046 §6.</summary>
    public bool HasNamedBlocks => Ast.BeginBlock is not null || Ast.ProcessBlock is not null || Ast.EndBlock is not null;

    /// <summary>源文件路径（脚本块来自 .openshell 文件时非空，REPL 内为 null）。Per ADR-0046 §10.</summary>
    /// <remarks>从 AST 的 SourceFile 字段读取（Parser 在创建脚本块 AST 时填充）。</remarks>
    public string? File => Ast.SourceFile;

    /// <summary>源文本起始位置。Per ADR-0046 §10.</summary>
    public SourcePosition StartPosition => Ast.Span.Start;

    /// <summary>源文本结束位置。Per ADR-0046 §10.</summary>
    public SourcePosition EndPosition => Ast.Span.End;

    /// <summary>
    /// 同步调用脚本块，返回最后一个表达式的值（OpenShell 单值语义）。
    /// 等价于 PowerShell 的 InvokeReturnAsIs。
    /// </summary>
    public object? Invoke(ExecutionContext? callerCtx = null, params object?[] args)
        => InvokeWithNamedArgs(callerCtx, namedArgs: null, args);

    /// <summary>
    /// PowerShell 兼容 API：仅返回最后一个值（不收集流式输出）。
    /// Per ADR-0046 §3.3. 等价于 <see cref="Invoke"/>（OpenShell 单值语义）。
    /// </summary>
    public object? InvokeReturnAsIs(params object?[] args) => Invoke(null, args);

    /// <summary>同步调用脚本块（带命名参数，含 -WhatIf / -Confirm 通用参数注入）。Per ADR-0049 §2.</summary>
    public object? InvokeWithNamedArgs(
        ExecutionContext? callerCtx = null,
        IReadOnlyDictionary<string, object?>? namedArgs = null,
        params object?[] args);

    /// <summary>作为 pipeline transform 流式执行。返回 IAsyncEnumerable&lt;IItem&gt;。Per ADR-0046 §5 + ADR-0048.</summary>
    public IAsyncEnumerable<IItem> InvokeStream(
        IAsyncEnumerable<IItem> input,
        ExecutionContext? ctx = null,
        CancellationToken ct = default);

    /// <summary>把脚本块包装为可步进管道。Per ADR-0046 §3.</summary>
    public SteppablePipeline GetSteppablePipeline(ExecutionContext? ctx = null)
        => new(this, ctx ?? CapturedContext);

    /// <summary>返回脚本块的原始源文本（含注释/空白/原始大小写），用于调试回显。Per ADR-0046 §2/§10.</summary>
    /// <remarks>从 AST 的 SourceText 字段读取；手工构造的 AST 回退为占位字符串。</remarks>
    public override string ToString() => Ast.SourceText ?? "<ScriptBlock>";
}
```

变量存储：根据 ADR-0047（revised typed variables），`[scriptblock]$sb` 强制类型约束。运行时 `ScriptBlock` 实例存于 `IVariableRegistry`，与 `string / int / IItem` 并列。`object` 兼容（脚本块是引用类型）。

```powershell
[scriptblock]$sb = { $_.Name }
$sb = "hello"      # 编译错误：string 不可隐式转 scriptblock
$sb = { $_.Size }  # OK
```

### 3. 脚本块调用

脚本块有三种调用方式，分别对应不同的求值语义。

#### 3.1 直接调用：`&` 调用运算符

```powershell
$sb = { "Hello" }
& $sb                          # 输出 "Hello"
& $sb -Prefix "Pre:"           # 传命名参数
$result = & $sb                # 捕获输出
$result = & { 1; 2; 3 }        # 内联脚本块
$result = & (Get-Block)        # 表达式返回脚本块后调用
```

`&` 后可跟：

| 形式 | 示例 | 说明 |
|---|---|---|
| 变量 | `& $sb` | 直接调用变量值 |
| 字面脚本块 | `& { ... }` | 内联定义并立即调用 |
| 表达式 | `& (Get-Block)` | 求值表达式得脚本块后调用 |
| 命令名 | `& Get-ChildItem` | 已有 M1 行为（命令调用） |
| 别名 | `& ls` | 别名展开后调用 |

参数绑定遵循 ADR-0024（revised）的函数参数绑定规则：位置参数 → `Position=N` 的形参；命名参数 → 同名形参；未绑定形参用默认值；多余位置参数进入 `$args` 自动变量。

#### 3.2 管道调用：`.GetSteppablePipeline()`

```powershell
$sb = { process { $_.Name } }
Get-ChildItem | & $sb
```

`GetSteppablePipeline()` 返回 `SteppablePipeline`，封装 `begin/process/end` 三段：

```csharp
public sealed class SteppablePipeline
{
    private readonly ScriptBlock _block;
    private readonly Scope _scope;
    private readonly object[] _args;
    private bool _beginExecuted;
    private bool _endExecuted;

    public SteppablePipeline(ScriptBlock block, Scope scope, object[] args)
    {
        _block = block;
        _scope = scope;
        _args = args;
    }

    public void Begin(params object[] args)
    {
        if (_block.Ast.HasBegin)
            _block.InvokeBegin(_scope, args);
        _beginExecuted = true;
    }

    public object? Process(object input)
    {
        if (!_beginExecuted) Begin(_args);
        if (_block.Ast.HasProcess)
            return _block.InvokeProcess(_scope, input);
        // 无 process 段时整体当 process：每个输入项求值一次
        return _block.InvokeBody(_scope, input);
    }

    public IEnumerable<object?> End()
    {
        if (_endExecuted) yield break;
        if (_block.Ast.HasEnd)
            foreach (var o in _block.InvokeEnd(_scope)) yield return o;
        _endExecuted = true;
    }
}
```

当 `& $sb` 出现在管道节点位置时，`PipelineExecutor`（ADR-0010 §2）调 `GetSteppablePipeline()` 而非 `Invoke()`：

```csharp
// ADR-0010 PipelineExecutor 内部
if (nodes[i].Command is ScriptBlock sb)
{
    var pipe = sb.GetSteppablePipeline();
    pipe.Begin();
    stream = TransformStream(stream, item =>
    {
        using (ctx.PushPipelineScope(item))   // $_ = item per ADR-0047
            return pipe.Process(item);
    });
    // End 在管道结束前调用
}
```

#### 3.3 方法式调用：`.Invoke()` / `.InvokeReturnAsIs()`

```powershell
$sb.Invoke("arg1", "arg2")
$sb.InvokeReturnAsIs("arg1")    # 仅返回最后值
```

- `.Invoke(...)`：返回最后一个表达式的值（OpenShell 单值语义，等价于 PowerShell 的 `InvokeReturnAsIs`）
- `.InvokeReturnAsIs(...)`：PowerShell 兼容别名，等价于 `.Invoke(...)`
- 两者均走 `param()` 参数绑定（§8）

方法式调用与 `&` 调用运算符的差异：

| 维度 | `& $sb` | `$sb.Invoke(...)` |
|---|---|---|
| 语法 | 运算符 | 方法 |
| 输出 | 直接流入管道 / 捕获 | 返回 IList |
| 管道绑定 | 可作为管道节点 | 不可作为管道节点 |
| 适用场景 | 脚本内惯用法 | C# 互操作 / 反射调用 |

### 4. 脚本块作为参数

#### 4.1 内置 cmdlet 形参

```powershell
# ADR-0012 修订版定义的 cmdlets
Get-ChildItem | Where-Object { $_.Length -gt 1MB }
Get-ChildItem | ForEach-Object { $_.Name }
Sort-Object -Property { $_.LastWriteTime }
Measure-Object -Property { $_.Length }
```

cmdlet 声明 `[scriptblock]` 类型形参：

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
        var bound = Args.Predicate;       // ScriptBlock 实例（编译一次）
        await foreach (var item in input.WithCancellation(ct))
        {
            using (ctx.PushPipelineScope(item))   // $_ = item per ADR-0047
            {
                if ((bool)bound.InvokeReturnAsIs()!)
                    yield return item;
            }
        }
    }
}
```

#### 4.2 用户函数脚本块形参

```powershell
function Invoke-Twice {
    param([scriptblock]$Block)
    & $Block
    & $Block
}

Invoke-Twice -Block { "Hello" }
```

- `[scriptblock]` 类型注解强制实参必须是脚本块（运行时类型检查）
- 字符串 `"hello"` 不可隐式转为脚本块（需显式 `Invoke-Expression` 或 `[scriptblock]::Create("...")`）
- 参数绑定：位置或命名（per ADR-0024 revised）

#### 4.3 参数绑定规则

- 位置参数 → `Position=N` 的形参
- 命名参数 → 同名形参
- `[switch]` 形参不接收值（出现即为 `$true`）
- 未绑定形参用默认值；默认值表达式每次调用时重新求值
- 多余位置参数进入 `$args` 自动变量（per ADR-0042 §2）

### 5. 管道变量 `$_` / `$PSItem`

- 在 `process` 块内，`$_` 是当前管道 `IItem`（per ADR-0010）
- `$PSItem` 是 `$_` 的别名（同一引用，二者完全等价）
- `$_.PropertyName` 访问 `IItem` 属性（per ADR-0047 revised）
- `$_.Method()` 调用对象方法（per ADR-0047）
- 管道外（如 `begin` 块、顶层 REPL、`end` 块内）：`$_` 为 `$null`
- `$_` 在 `process` 块内只读，赋值抛 `ReadOnlyVariableException`（per ADR-0042 §2 自动变量语义）

```csharp
// ADR-0010 PipelineExecutor 调用脚本块时压入 pipeline scope
using (ctx.PushPipelineScope(item))
{
    // 此作用域内：$_ == item, $PSItem == item
    bound.InvokeReturnAsIs();
}
// 离开作用域后：$_ 恢复为外层绑定（通常 $null）
```

`PushPipelineScope` 内部把 `$_` 与 `$PSItem` 都绑定到 `item`，标记为只读；离开 `using` 块时弹出作用域，恢复外层 `$_` 绑定（通常是 `$null`）。

### 6. `begin` / `process` / `end` 块

脚本块可包含三个命名子块：

```powershell
$sb = {
    begin { $count = 0 }
    process { $count++; $_.Name }
    end { Write-Verbose "Processed $count items" }
}

Get-ChildItem | & $sb
```

执行语义：

| 子块 | 触发时机 | 是否可访问 `$_` | 是否可访问 `$count`（局部） |
|---|---|---|---|
| `begin` | 管道开始前一次 | 否（`$_` = `$null`） | 是 |
| `process` | 每个管道项一次 | 是（`$_` = 当前项） | 是 |
| `end` | 管道结束后一次 | 否（`$_` 恢复 `$null`） | 是 |

边界规则：

- 无管道输入时，`process` 仍执行一次，`$_` = `$null`（PowerShell 兼容）
- 若脚本块无 `begin/process/end`（仅普通语句），整体视为 `process` 块：每个管道项求值一次
- `begin` / `process` / `end` 在同一脚本块内最多各出现一次，重复声明报 `ParseException`
- 子块之间共享脚本块局部作用域（`begin` 设的 `$count` 在 `process` / `end` 可见）
- `process` 子块内 `return` 提前结束当前项处理（per ADR-0045）
- `end` 子块内 `return` 提前结束整个脚本块（不再处理后续管道项）

### 7. 闭包与作用域捕获

脚本块是闭包：捕获定义时的词法作用域。

```powershell
function Get-Counter {
    $n = 0
    return {
        $n++
        $n
    }
}

$counter = Get-Counter
& $counter    # 1
& $counter    # 2
& $counter    # 3
```

变量查找顺序：

1. 脚本块局部作用域（`param()` 与脚本块内赋值）
2. 捕获的作用域（定义时的词法栈帧，存于 `ScriptBlock._capturedScope`）
3. 沿作用域链向上（Session → Script → Global per ADR-0042 §5）

```powershell
# 多层闭包
function Get-Adder {
    param([int]$base)
    return {
        param([int]$x)
        $base + $x
    }
}

$add10 = Get-Adder 10
& $add10 5    # 15
```

`$using:` 修饰符用于把外层变量显式传入远程 / 并行作用域：

```powershell
$threshold = 1MB
Invoke-Command -ComputerName "srv01" { $_.Length -gt $using:threshold }
Start-Job { Get-ChildItem | Where-Object { $_.Length -gt $using:threshold } }
```

`$using:X` 在脚本块跨越作用域边界（远程会话、后台作业、并行 foreach）时由运行时拷贝 `X` 的值到目标作用域。本地脚本块内 `$using:` 不需要（直接闭包捕获即可），本地使用 `$using:` 报 `ParseException`。

闭包实现：

```csharp
// Parser 构造脚本块时捕获当前 Scope 引用
public ScriptBlock CreateScriptBlock(ScriptBlockAst ast, string source, Scope currentScope)
{
    return new ScriptBlock(
        ast,
        capturedScope: currentScope,   // 闭包捕获
        sourceText: source,
        file: currentScope.SourceFile,
        startPosition: ast.Start,
        endPosition: ast.End);
}
```

### 8. `param()` 块

```powershell
$sb = {
    param(
        [Parameter(Mandatory, Position=0)]
        [string]$Name,
        
        [Parameter(Position=1)]
        [int]$Count = 1,
        
        [switch]$Force
    )
    
    for ($i = 0; $i -lt $Count; $i++) {
        "Hello, $Name" + $(if ($Force) { "!" } else { "." })
    }
}

& $sb "World" 2 -Force
# Hello, World!
# Hello, World!
```

- 参数绑定规则与 ADR-0024（revised）的函数参数完全一致
- `[Parameter()]` 特性声明位置、是否必填、是否从管道绑定
- `[switch]` 类型形参不接收值（出现即为 `$true`）
- 默认值表达式在脚本块每次调用时求值
- 通用参数（`-Verbose` / `-Debug` / `-WhatIf` / `-Confirm` per ADR-0049）自动可用

`param()` 块在脚本块 AST 中位于 `ScriptBlockAst.Parameters`，与体部语句分离：

```csharp
public sealed class ScriptBlockAst : Ast
{
    public ParameterBlockAst? Parameters { get; }     // null 表示无 param() 块
    public IReadOnlyList<StatementAst> Body { get; }
    public BlockAst? BeginBlock { get; }
    public BlockAst? ProcessBlock { get; }
    public BlockAst? EndBlock { get; }
    public bool HasBegin => BeginBlock is not null;
    public bool HasProcess => ProcessBlock is not null;
    public bool HasEnd => EndBlock is not null;
    public bool HasNamedBlocks => HasBegin || HasProcess || HasEnd;
}
```

### 9. 脚本块作为值

脚本块是一等值，享有以下能力：

| 操作 | 支持 | 说明 |
|---|---|---|
| 赋值给变量 | ✅ | `$sb = { ... }` |
| 作为函数实参 | ✅ | `Invoke-Twice -Block { ... }` |
| 作为函数返回值 | ✅ | `return { ... }` |
| 存入数组 | ✅ | `@( { ... }, { ... } )` |
| 存入哈希表值 | ✅ | `@{ Block = { ... } }` |
| 作为哈希表键 | ❌ | 哈希表键必须为 `string`（per ADR-0042 §3） |
| 写入 `$env:` | ❌ | 环境变量为 `string`，不接受脚本块 |
| 类型注解约束 | ✅ | `[scriptblock]$sb` 强制类型 |
| 持久化到 `variables.toml` | ❌ | 脚本块不可序列化为 TOML |

相等性：

```powershell
$sb1 = { $_.Name }
$sb2 = { $_.Name }
$sb1 -eq $sb2    # $false（按引用比较，不同实例）
$sb1 -eq $sb1    # $true（同一实例）
```

两个脚本块即使源文本完全相同，也是不同的 `ScriptBlock` 实例，按引用相等。这避免了对脚本块进行"结构相等"的复杂语义（PowerShell 也是引用相等）。

### 10. AST 与反射

脚本块暴露 AST 供工具消费：

```powershell
$sb = { $_.Name.ToUpper() }
$sb.Ast                          # 根 AST 节点
$sb.Ast.Body                     # 体部语句列表
$sb.Ast.Body[0].Expression       # 第一条语句的表达式
$sb.ToString()                   # 原始源文本 "{ $_.Name.ToUpper() }"
$sb.File                         # 来源文件（REPL 内为 $null）
$sb.StartPosition                # 源文本起始位置（行、列）
$sb.EndPosition                  # 结束位置
```

AST 节点类型在 `OpenShell.Core.Scripting.Ast` 命名空间：

```csharp
namespace OpenShell.Core.Scripting.Ast;

public abstract class Ast
{
    public TextSpan Start { get; }
    public TextSpan End { get; }
    public abstract void Accept(AstVisitor visitor);
}

public sealed class ScriptBlockAst : Ast
{
    public ParameterBlockAst? Parameters { get; }
    public IReadOnlyList<StatementAst> Body { get; }
    public BlockAst? BeginBlock { get; }
    public BlockAst? ProcessBlock { get; }
    public BlockAst? EndBlock { get; }
}

public sealed class ParameterBlockAst : Ast
{
    public IReadOnlyList<ParameterAst> Parameters { get; }
}

public sealed class ParameterAst : Ast
{
    public TypeConstraint? Type { get; }
    public string Name { get; }
    public ExpressionAst? DefaultValue { get; }
    public IReadOnlyList<ParameterAttribute> Attributes { get; }
}

public abstract class StatementAst : Ast;
public sealed class ExpressionStatementAst : StatementAst { /* ... */ }
public sealed class IfStatementAst : StatementAst { /* ... */ }     // ADR-0045
public sealed class ForStatementAst : StatementAst { /* ... */ }    // ADR-0045
public sealed class WhileStatementAst : StatementAst { /* ... */ }  // ADR-0045
public sealed class TryStatementAst : StatementAst { /* ... */ }   // ADR-0045
// ... 其他控制流语句（ADR-0045）
```

消费者：

- **IDE 语法高亮**：`$sb.Ast` 遍历定位 token 类型
- **`Get-Command -Syntax`**：从脚本块 `param()` 生成参数签名
- **调试器**：在 `StartPosition` / `EndPosition` 之间设置断点
- **重构工具**：基于 AST 重命名 `$_.Name` → `$_.FullName` 等
- **`$sb.ToString()` 必须保留原始源文本**（包括注释、空白、原始大小写），用于调试时回显

### 11. 安全

脚本块允许任意计算（包括调用 cmdlet、访问文件、网络、进程生成等），因此安全责任不在脚本块层，而下沉到命令级沙箱：

- **Provider 沙箱**（ADR-0036 §6）：脚本块内调用的 cmdlet 必须遵守 Provider 的 `ProviderSandbox` 声明
- **风险等级**（ADR-0036 §1）：脚本块内的破坏性操作（`remove-item` / `set-content`）走标准风险等级确认
- **进程生成权限**（ADR-0036 §12）：脚本块内的 `start-process` 受 `ProcessSpawn` 权限约束
- **网络访问**（ADR-0036 §11）：脚本块内的 `invoke-webrequest` 受 Provider `NetworkAccess` 约束
- **审计**（ADR-0036 §5）：脚本块内的高风险操作自动写入 `audit.jsonl`

来源不可信的脚本块（如下载的 `.openshell` 脚本）需通过 `ExecutionPolicy` 控制（未来 ADR）：

- `Restricted`：禁止执行任何 `.openshell` 文件中的脚本块
- `RemoteSigned`：本地脚本块可直接执行，远程来源脚本块需有数字签名
- `Unrestricted`：所有脚本块可执行，但远程来源会弹确认提示
- `Bypass`：无任何限制（仅用于测试环境）

`ExecutionPolicy` 的具体机制由独立 ADR 定义，本 ADR 仅说明脚本块作为执行单元需要受策略约束。

## Alternatives Considered

1. **不支持脚本块（M1 设计 / ADR-0024 首版）**：被否决（2026-07-08）。理由：阻断 PowerShell 全兼容目标，`Where-Object { ... }` / `ForEach-Object { ... }` / `Invoke-Command { ... }` 等核心场景无法实现，用户被迫学习新 DSL。安全责任可由 ADR-0036 命令级沙箱承担，无需在语言层禁止。

2. **Lambda-only（C# 风格 `x => x.Name`）**：被否决。理由：
   - 语法与 PowerShell 不兼容，PS 用户迁移成本高
   - 不支持多语句体（C# lambda 单表达式限制）
   - 不支持 `begin/process/end` 三段
   - 不支持 `param()` 块与命名参数
   - 与 PowerShell `[scriptblock]` 类型系统不互通

3. **嵌入 Lua / JavaScript（NLua / JINT）**：被否决。理由：
   - 语法差异（`function() end` / `function() {}` 与 `{ }` 不同）
   - 类型系统不对齐（Lua 的 table、JS 的 object 与 `IItem` 双重模型）
   - 引入外部运行时依赖，增加打包体积
   - 安全沙箱与 .NET 类型系统隔离困难

4. **脚本块不带闭包（定义时快照变量值）**：被否决。理由：
   - 破坏 PowerShell 闭包惯用法（`Get-Counter` / `Get-Adder` 等模式无法实现）
   - 循环中创建脚本块时无法捕获循环变量最新值
   - 与 ADR-0024（revised）函数定义语义不一致

5. **AST 解释执行（不编译为委托）**：被否决。理由：
   - 性能：每次调用重新走 AST 树，约 10× 编译后调用开销
   - 调试体验差（无法在 AST 节点设断点）
   - 改用编译为表达式树 + 缓存策略，与 ADR-0012 §8 AST 缓存一致

6. **仅支持命令式脚本块（不支持 `param()` / `begin/process/end`）**：被否决。理由：
   - 不兼容 PowerShell 高级函数与 cmdlet 写法
   - `Where-Object { begin{} process{} end{} }` 等高级用法无法迁移
   - 与 ADR-0024（revised）`function` 关键字定义的高级函数语义割裂

7. **脚本块不可作为值（仅内联传给 cmdlet）**：被否决。理由：
   - 无法支持 `Invoke-Command $sb` 等延迟调用场景
   - 无法支持 `$sb = { ... }; & $sb` 模式
   - 失去一等值的核心优势，与 PowerShell 模型不兼容

8. **脚本块按结构相等（源文本相同即相等）**：被否决。理由：
   - 与 PowerShell 引用相等的语义不一致
   - AST 节点位置不同（不同文件 / 不同行）会导致"相同"脚本块实际行为不同
   - 哈希困难（AST 节点深度哈希成本高）

## Consequences

### 优势

- **PowerShell 全兼容**：`{ ... }` 语法、`&` 调用、`begin/process/end`、`param()`、闭包全部对齐，PS 用户零成本迁移
- **一等值表达力**：脚本块可作为变量值、参数、返回值、集合元素，支持高阶函数、回调、策略模式等惯用法
- **统一求值引擎**：ADR-0012 修订版的 `Where-Object` / `ForEach-Object` / `Sort-Object` 与 ADR-0024 修订版的 `function` 都基于本 ADR 的脚本块引擎，避免重复实现
- **AST 反射**：调试器、IDE、`Get-Command -Syntax` 可基于 AST 工具化
- **闭包语义清晰**：词法作用域捕获 + `$using:` 跨作用域传值，覆盖本地与远程场景
- **与变量系统对齐**：`$_` / `$PSItem` 通过 ADR-0047 作用域栈绑定，复用已有机制
- **管道复用**：脚本块在管道内编译一次、每元素复用，避免重复解析开销

### 代价

- **解析器复杂度**：`{ }` 作为新 token 引入，需处理嵌套（脚本块内脚本块）、与哈希表 `@{ }` 的区分、与代码块的歧义（如 `if (...) { }` 的代码块 vs 脚本块）
- **求值器实现量**：AST 编译器、闭包捕获、`param()` 绑定、`SteppablePipeline` 等约 2000 行 C# 代码
- **性能开销**：脚本块调用比直接命令调用慢约 10μs（AST 编译缓存后），可接受
- **源文本保留**：Parser 必须保留 `{ ... }` 的原始字符（含注释、空白），增加内存开销
- **安全责任转移**：脚本块允许任意代码，安全模型必须依赖 ADR-0036 命令级沙箱，不能在脚本块层做静态分析
- **闭包内存泄漏风险**：脚本块持有捕获作用域引用，长生命周期脚本块可能延迟 GC，需在调试器中可视化引用链

### 约束

- `{` 与 `}` 是保留 token，不能用作命令名或裸运算符
- 脚本块必须可重新序列化为源文本（`$sb.ToString()` 返回原始文本，用于调试）
- `$_` 在 `process` 块内只读，赋值抛 `ReadOnlyVariableException`
- `[scriptblock]` 类型注解强制实参必须是 `ScriptBlock` 实例，运行时类型检查
- `param()` 块若存在必须为脚本块体部的第一条语句，否则 `ParseException`
- `begin` / `process` / `end` 在同一脚本块内最多各一次，重复声明 `ParseException`
- 脚本块相等性按引用比较，禁止重写 `Equals` / `GetHashCode`
- 脚本块不可作为哈希表键（哈希表键必须为 `string`，per ADR-0042 §3）
- 脚本块不可写入 `$env:`（环境变量为 `string`，类型不匹配）
- `$using:` 修饰符仅在远程 / 并行作用域生效，本地脚本块内使用报 `ParseException`
- 脚本块内 cmdlet 调用受 ADR-0036 沙箱约束，禁止逃逸操作（文件写、网络、进程生成等需对应权限）
- 远程来源（下载的 `.openshell` 脚本）的脚本块受 `ExecutionPolicy` 约束（未来 ADR）
- `ScriptBlock` 类必须 `sealed`，禁止用户继承
- `ScriptBlockAst` 与子节点必须 `sealed` 或 `record`，保证 AST 不可变
- AST 节点必须实现 `Accept(AstVisitor)` 接受访问者（用于工具消费）
- 脚本块实例在管道内编译一次，每元素复用（同 ADR-0012 §8 AST 缓存策略）
- `SteppablePipeline` 必须按 `Begin → Process×N → End` 顺序调用，跳过 `Begin` 直接 `Process` 报 `InvalidOperationException`
- `InvokeReturnAsIs` 必须返回最后一条语句的结果，不收集流式输出
- `GetSteppablePipeline` 在脚本块无 `process` 块时把体部当 `process` 段（PowerShell 语义）
- `$PSItem` 必须与 `$_` 完全等价（同引用、同赋值语义、同只读约束）
- 空脚本块 `{ }` 求值返回 `$null`，不抛异常
- 脚本块体部为空且无 `begin/process/end` 时，作为 `process` 段每管道项求值一次返回 `$null`
