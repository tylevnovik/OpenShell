# ADR-0051: async/await 语言支持

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M4 (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0050 (Modern Syntax), ADR-0044 (Task Center / Background Jobs)
- **Implementation Status**: M4 已实现 (2026-07-08): §1-§3 async fn / await expr / async { } 语法、IsAsync ScriptBlock 标记、Task<object?> 包装、UnwrapAwaitable 同步解包。Shell 单线程同步模型下 await 等价 .GetAwaiter().GetResult()。

## Context

OpenShell 的现代语法（ADR-0050）已引入 `fn` / `match` / `lambda` / `?.` 等现代化构造，但缺少异步编程支持。用户在以下场景需要异步能力：

1. **调用 .NET async API**：`[System.Net.Http.HttpClient]::GetAsync(url)` 返回 `Task<HttpResponseMessage>`，shell 用户需要一种方式等待结果。
2. **并发 I/O**：批量 HTTP 请求、文件异步读写等场景中，`async`/`await` 模式比 `Start-Job` / `Get-Job` 轻量得多。
3. **IAsyncEnumerable 流式处理**：.NET 的 `IAsyncEnumerable<T>` 是异步流，与 OpenShell 管道 `IAsyncEnumerable<IItem>` 天然对齐（per ADR-0010）。

### Shell 的同步语义约束

与 C#/JavaScript/Python 不同，shell 是**单线程同步**的执行模型：

- REPL 逐行读取、求值、输出，不存在事件循环（event loop）。
- 管道 `a | b | c` 是同步流式处理，每个命令的 `process` 块在前一个命令产出项后立即执行。
- 后台任务（`command &`，per ADR-0044 §11）通过 `ITaskCenter` + `Task.Run` 实现，但调用方仍同步等待 `Get-Job` / `Wait-Job`。

因此 OpenShell 的 `await` **不等价**于 C# 的异步等待（不释放线程、不回到 SynchronizationContext），而是等价于 `.GetAwaiter().GetResult()`——**同步阻塞**直到 Task 完成。这保证了：

- `await` 可在任意位置使用（不限于 `async` 函数体内）。
- 不引入事件循环、不改变控制流语义、不破坏 ADR-0045 控制流信号传播。
- 异常直接抛出（与 `throw` 信号一致），无需 `AggregateException` 解包。

### 与 ADR-0044 的关系

ADR-0044 §11 定义了 `command &` 后台执行与 `ITaskCenter` 任务管理。`async`/`await` 与之互补：

| 维度 | `command &` (ADR-0044) | `async`/`await` (本 ADR) |
|---|---|---|
| 执行模型 | 后台 Task + ITaskCenter 管理 | Task.Run 包装，await 同步取结果 |
| 适用场景 | 长时间管道命令 | .NET async API 调用、并发 I/O |
| 任务管理 | Get-Job / Wait-Job / Remove-Job | await 表达式内联 |
| 取消 | CancellationToken via ITaskCenter | CancellationToken via ExecutionContext |
| 管道集成 | 后台管道作为独立作业 | async 函数返回 Task，await 解包为值 |

## Decision

在现代语法（`.osh`）中引入 `async fn` / `await expr` / `async { }` 三种构造，基于 `Task<object?>` 包装与同步解包实现。

### 1. async fn — 异步函数声明

#### 语法

```
async fn name(params) { body }
async fn fetch(url: string) {
    let client = [System.Net.Http.HttpClient]::new()
    let resp = await client.GetAsync(url)
    return resp.Content.ReadAsStringAsync().Result
}
```

#### 语义

- `async fn` 声明一个函数，其函数体被标记为 `IsAsync = true` 的 `ScriptBlock`。
- **调用时**不立即执行函数体，而是返回 `Task<object?>`。函数体的实际执行延迟到 `await` 时（lazy 语义）。
- 实现方式：`InvokeCommand` 检测 `sb.IsAsync == true` 时，路由到 `InvokeAsyncScriptBlock`，在 `Task.Run` 内调用 `InvokeWithNamedArgs`。

#### AST 节点

```
AsyncFunctionDeclarationAst(
    string Name,
    IReadOnlyList<ParameterDeclaration> Parameters,
    ScriptBlockExpression Body,
    SourceSpan Span) : Statement
```

#### 求值规则

1. `EvaluateAsyncFunctionDeclaration`：构造 `ScriptBlock(body, ctx) { IsAsync = true }`，注册到变量表。
2. 调用 `async fn` 定义的函数时，`InvokeCommand` 检测 `IsAsync`，调用 `InvokeAsyncScriptBlock`：
   ```
   Task.Run<object?>(() => sb.InvokeWithNamedArgs(ctx, namedArgs, args), ctx.CancellationToken)
   ```
3. 返回值是 `Task<object?>`，调用方需 `await` 取实际结果。

### 2. await expr — 等待异步结果

#### 语法

```
let result = await asyncFunc(args)
let data = await httpClient.GetAsync(url)
let lines = await asyncStream  # IAsyncEnumerable<IItem>
```

#### 语义

- `await` 是**一元前缀运算符**，对表达式求值后同步解包 awaitable 值。
- Shell 单线程同步模型下，`await` 等价 `.GetAwaiter().GetResult()`——阻塞当前线程直到完成。
- `await` 可在**任意表达式位置**使用（不限于 `async` 函数体内），这是与 C# 的关键差异。

#### UnwrapAwaitable 解包规则

| 操作数类型 | 解包方式 | 返回值 |
|---|---|---|
| `Task<T>` | `.GetAwaiter().GetResult()` | `T` |
| `Task` (void) | `.GetAwaiter().GetResult()` | `null` |
| `ValueTask<T>` | `.GetAwaiter().GetResult()` | `T` |
| `ValueTask` | `.GetAwaiter().GetResult()` | `null` |
| `IAsyncEnumerable<IItem>` | 同步收集到 `List<object?>` | `List` |
| 嵌套 `Task<Task<T>>` | 递归解包直到非 Task | 最内层 `T` |
| 非 awaitable | 原样返回 | 原值 |

#### AST 节点

```
AwaitExpressionAst(Expression Operand, SourceSpan Span) : Expression
```

#### 求值规则

1. `EvaluateAwait`：先求值 `Operand` 得到操作数。
2. 调用 `UnwrapAwaitable(operand)`：
   - `Task` → 访问 `Result` 属性（void Task 返回 null）。
   - `ValueTask` → 反射调用 `GetAwaiter().GetResult()`。
   - `IAsyncEnumerable<IItem>` → 同步遍历收集到 `List`。
   - 递归：如果解包结果仍是 Task，继续解包。
3. 返回解包后的值。

#### 异常处理

- Task 内抛出的异常在 `GetAwaiter().GetResult()` 时以原始异常形式抛出（非 `AggregateException`）。
- 异常通过 `OpenShellScriptException` 包装，与 ADR-0045 §13 throw 信号传播一致。
- `try { await risky() } catch { }` 可正常捕获异步异常。

### 3. async { } — 异步块表达式

#### 语法

```
let task = async {
    let a = computeA()
    let b = computeB()
    a + b
}
let result = await task
```

#### 语义

- `async { }` 是**表达式**，求值时返回 `Task<object?>`，体部延迟到 `await` 时执行。
- 实现方式：`Task.Run` 内构造子 `Evaluator`，执行块内 Statements，返回最后表达式的值。
- 捕获当前 `ExecutionContext`（含变量作用域、命令注册表等），在 Task 内复用。

#### AST 节点

```
AsyncBlockExpression(
    IReadOnlyList<Statement> Statements,
    SourceSpan Span) : Expression
```

#### 求值规则

1. `EvaluateAsyncBlock`：捕获当前 `_ctx`。
2. 构造 `Task.Run<object?>(() => { ... })`：
   - 在 Task 内创建 `new Evaluator(capturedCtx)`。
   - 构造 `ScriptBlockAst(statements, [], span)`。
   - 调用 `evaluator.Execute(scriptAst)`。
   - 如果 `result.Signal == Throw`，抛出 `OpenShellScriptException`。
   - 返回 `result.Value`。
3. 返回 `ExecutionResult.Of(task)`（task 是 `Task<object?>`）。

### 4. ScriptBlock.IsAsync 标记

`ScriptBlock` 类型新增 `IsAsync` 属性（`bool`，默认 `false`，`init` set）：

```
public sealed class ScriptBlock
{
    public bool IsAsync { get; init; }
    // ...
}
```

- `async fn` 声明时设 `IsAsync = true`。
- 普通 `fn` / `function` 声明时 `IsAsync = false`（默认）。
- `InvokeCommand` 在解析命令到 ScriptBlock 后检查 `IsAsync`：
  - `false` → 同步调用 `InvokeWithNamedArgs`，返回值直接作为结果。
  - `true` → 调用 `InvokeAsyncScriptBlock`，返回 `Task<object?>`。

### 5. 与管道的交互

- `async fn` 可作为管道命令使用：`items | asyncFn | process`。
- 管道执行器（`EvaluatePipeline`）对 `async fn` 的处理：每个输入项调用 `InvokeAsyncScriptBlock`，得到 `Task<object?>`，同步 await 后传入下一阶段。
- `IAsyncEnumerable<IItem>` 流（per ADR-0010）可通过 `await` 收集为 `List`，再参与管道。

### 6. 取消传播

- `async { }` 块和 `async fn` 调用都绑定 `ExecutionContext.CancellationToken`。
- `Task.Run(..., ctx.CancellationToken)` 确保取消令牌传递到后台任务。
- 用户 `Ctrl+C` 触发取消时，后台 Task 收到 `OperationCanceledException`，`await` 时抛出。

## Consequences

1. **新增 3 个 AST 节点**：`AsyncFunctionDeclarationAst` / `AwaitExpressionAst` / `AsyncBlockExpression`。
2. **ScriptBlock 扩展**：新增 `IsAsync` init 属性，`InvokeCommand` 增加异步路由分支。
3. **Tokenizer 扩展**：`async` / `await` 作为现代语法关键字（per ADR-0050 §1.2 keyword 表）。
4. **不引入事件循环**：`await` 是同步阻塞，不改变 shell 的单线程执行模型。
5. **JIT 编译器排除**（per ADR-0058）：`AwaitExpressionAst` / `AsyncBlockExpression` 不被 `ExpressionCompiler` 编译（含控制流副作用），回退到解释执行。
6. **REPL 友好**：`await` 可在 REPL 顶层直接使用，无需包裹在 `async` 函数内。

## Open Questions

1. **并发 await**：当前 `await` 是串行阻塞。未来是否支持 `await Promise.all([task1, task2])` 风格的并发等待？（M5+ 考虑）
2. **async stream 管道**：`IAsyncEnumerable<IItem>` 直接作为管道源（不 collect 到 List）的流式 await 语义。（M5+ 考虑）
3. **async using / finally**：`async using` 资源释放与 `try-finally` 中的 async 语义。（暂不需要）
