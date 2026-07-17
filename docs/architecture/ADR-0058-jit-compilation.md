# ADR-0058: JIT 编译策略 — AST 到委托缓存与热点路径检测

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M5+ (Performance)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0045 (Control Flow), ADR-0046 (Script Blocks), ADR-0047 (Variable System), ADR-0050 (Modern Syntax), ADR-0051 (Async/Await)
- **Implementation Status**: M5+ 已实现 (2026-07-08): §1-§7 完整 JIT 框架, 包含 ICompilationCache / InMemoryCompilationCache, HotPathTracker (基于调用计数 + 时间窗口的 hotspot 识别), ExpressionCompiler (LiteralExpression / VariableExpression / BinaryExpression / UnaryExpression / MemberExpression / IndexExpression / CastExpression / SubExpression / ArrayExpression / RangeExpression / TernaryExpression 到 Delegate 编译, 不支持的节点抛 NotSupportedException 由 Evaluator 回退到解释执行), Evaluator.EvaluateExpression 集成 (Tier 0 解释执行, 命中阈值后转 Tier 1 编译委托, Tier 2 优化预留), AddJitCompilation() DI 扩展。

## Context

OpenShell 当前 (M4 语言层推进完成、M5 性能层启动) 的执行模型为**纯 AST 解释执行**: 每次 `Evaluator.EvaluateExpression` 都对 AST 节点做 `switch` 模式匹配并递归求值。该模型在以下场景存在显著性能瓶颈:

1. **热点循环**: `1..10000 | ForEach-Object { $_ * 2 + 1 }` 这类管道循环对每个元素重新走 AST 解释路径, 每次调用 `{ $_ * 2 + 1 }` 脚本块都重新 dispatch `EvaluateExpression(BinaryExpression)` → `EvaluateBinary` → 类型检查 → `EvaluateExpression(VariableExpression)` → ...
2. **脚本块高频调用**: `Get-ChildItem | Where-Object { $_.Length -gt 1MB }` 中, `Where-Object` 的 ScriptBlock 对每个文件项调用一次, 每次调用 8-15 次 AST 节点 dispatch
3. **基准差距**: 纯解释执行的 hot loop 吞吐约 100K-500K ops/s; PowerShell 7 的 compiled ScriptBlock 约 5M-20M ops/s; 差距 10-50 倍
4. **lambda 内联**: ADR-0050 §6 的 `lambda` 与 ADR-0051 §3 的 `async` 块每次调用都重新构造闭包, 无内联优化
5. **REPL 重复求值**: REPL 中粘贴一段表达式反复执行 (调试 / 探索) 时, 每次都重新 AST 解释

### 设计原则

1. **渐进式**: 不一次性替换解释器, 而是分层 (Tier 0 解释 → Tier 1 编译委托 → Tier 2 优化) 渐进提升
2. **安全回退**: 编译器不支持某 AST 节点时抛 `NotSupportedException`, Evaluator 捕获后回退到解释执行, **保证语义不丢失**
3. **缓存命中零成本**: 已编译的委托直接 `Delegate.Invoke(args)`, 不再走 AST switch
4. **热点检测精准**: 基于调用计数 + 时间窗口的 hotspot 识别, 避免编译一次性执行的冷代码
5. **不破坏 ADR-0046 闭包语义**: 编译委托需捕获与解释执行相同的 `ExecutionContext`, 闭包变量、`$_` 管道项、`$using:` 等语义一致
6. **观察性**: 缓存命中率、编译次数、回退次数可通过 `Get-JitStats` 命令 (未来 ADR) 查询
7. **内存可控**: `ICompilationCache` 默认 LRU + 容量上限, 避免长期运行的进程无限增长

### 依赖关系

- **上游依赖**:
  - ADR-0045 (Control Flow): 编译器需正确传播 `FlowSignalKind` (return / break / continue / throw / exit)
  - ADR-0046 (Script Blocks): ScriptBlock 是一等公民, 编译委托与 `ScriptBlock.Invoke` 语义对齐
  - ADR-0047 (Variable System): 变量作用域栈 (Global / Script / Local / Private / Using) 在编译委托内需正确访问
  - ADR-0050 (Modern Syntax): 三元 / lambda / match 等 modern 表达式的编译路径
  - ADR-0051 (Async/Await): `await` / `async` 块的编译策略 (本 ADR 仅编译同步部分, async 部分仍走解释)
- **下游依赖**:
  - 所有 `ScriptBlock.Invoke` 调用方 (PipelineExecutor / Where-Object / ForEach-Object / Invoke-Command)
  - 未来 ADR (Tier 2 优化): 动态方法 IL emit / LINQ表达式树优化

## Decision

### 1. 三层执行模型

OpenShell 表达式执行采用分层 JIT 模型, 与 V8 / .NET RyuJIT 的 tiered compilation 思路对齐:

| Tier | 名称 | 触发条件 | 实现 | 性能 |
|---|---|---|---|---|
| Tier 0 | 解释执行 | 首次调用 / 冷代码 | `Evaluator.EvaluateExpression` AST switch | 100K-500K ops/s |
| Tier 1 | 委托缓存 | 调用次数 ≥ 阈值 (默认 32) | `ExpressionCompiler.Compile` → `Func<ExecutionContext, object?>` | 1M-5M ops/s |
| Tier 2 | 优化编译 | 调用次数 ≥ 高阈值 (默认 1024) + 类型稳定 (预留) | IL emit / Expression Tree (未来 ADR) | 5M-20M ops/s |

```csharp
public enum CompilationTier
{
    Interpreted = 0,    // Tier 0
    Compiled = 1,       // Tier 1: 委托缓存
    Optimized = 2,      // Tier 2: IL emit (预留)
}
```

**触发流程**:

1. 首次调用: `Evaluator.EvaluateExpression` 走 Tier 0 解释路径, 同时 `HotPathTracker.RecordInvocation(ast)` 记录调用
2. 调用次数达阈值 (`HotPathThreshold = 32`): `ExpressionCompiler.TryCompile(ast, out var del)` 尝试编译
   - 编译成功: 缓存委托到 `ICompilationCache`, 后续调用走 `del(ctx)`
   - 编译失败 (节点不支持): 标记 `ast` 为 uncacheable, 后续不再尝试编译
3. 调用次数达高阈值 (`OptimizationThreshold = 1024`): 触发 Tier 2 编译 (本 ADR 仅预留, 实际仍返回 Tier 1 委托)

### 2. ExpressionCompiler

`ExpressionCompiler` 是 AST → `Func<ExecutionContext, object?>` 委托的编译器。**仅支持纯表达式节点** (无副作用语义可静态证明的节点), 不支持的节点抛 `NotSupportedException`。

```csharp
public sealed class ExpressionCompiler
{
    public bool TryCompile(Expression expr, out Func<ExecutionContext, object?> del);
}
```

#### 2.1 支持的节点类型

| AST 节点 | 编译策略 | 备注 |
|---|---|---|
| `LiteralExpression` | 闭包捕获常量值, 返回 `() => value` | 数字 / 字符串 / bool / null |
| `VariableExpression` | 生成 `ctx => ctx.Variables?.Get(name, scope)` | 含 `$global:` / `$script:` / `$local:` / `$_` / `$using:` |
| `BinaryExpression` | 递归编译 Left / Right, 组合为 `(l, r) => ApplyOperator(op, l, r)` | 算术 / 比较 / 逻辑 / 位运算 |
| `UnaryExpression` | 递归编译 Operand, 包装 `UnaryOp` 调用 | `-not` / `!` / `~` / `-` / `+` / `++` / `--` |
| `MemberExpression` | 编译 Target + Arguments, 反射调用属性 / 方法 | 复用 Evaluator.EvaluateMember 逻辑 |
| `IndexExpression` | 编译 Target + Index, 反射调用索引器 | 数组 / 哈希 / 字符串 |
| `CastExpression` | 编译 Operand, 包装 `Convert.ChangeType` | 类型转换 |
| `SubExpressionExpression` | 直接编译 Inner | `$(...)` |
| `ArrayExpression` | 编译每个 Element, 组合为 `object[]` | `@(...)` |
| `RangeExpression` | 编译 Start / End, 调用 `BuildRange` | `1..10` |
| `TernaryExpression` | 编译 Condition / IfTrue / IfFalse, 组合条件分支 | `cond ? a : b` |

#### 2.2 不支持的节点 (抛 NotSupportedException)

以下节点包含**语句级副作用**或**控制流信号**, 编译为纯委托会丢失语义, 由 Evaluator 回退到解释执行:

- `PipelineExpression`: 含命令调用, 可能修改变量 / 抛错误 / 产生输出流
- `CommandExpression`: 同上
- `ScriptBlockExpression`: 脚本块是数据, 不是表达式求值 (ScriptBlock 在被 Invoke 时才编译)
- `AssignmentExpression`: 赋值有副作用 (修改变量), 且 `+=` `-=` 等复合赋值需读改写
- `LambdaExpression`: lambda 体可能含任意语句, 不在表达式编译范围
- `MatchExpression`: 含 pattern matching, 涉及 `$matches` 自动变量副作用
- `HashExpression`: 哈希字面量编译复杂 (键可为表达式), 暂不支持
- `AwaitExpressionAst`: async 语义需 `Task` 状态机, 不在同步编译范围
- `AsyncBlockExpression`: 同上

**注**: `ScriptBlockExpression` 虽然不直接编译, 但当 ScriptBlock 被 `Invoke` 时 (per ADR-0046 §5), `ScriptBlock.Invoke` 内部会调用 `ExpressionCompiler.TryCompile(ScriptBlockAst, out var del)` 对**脚本块体**做整体编译 (而非单表达式), 此路径在本 ADR 仅预留接口, 实际仍走解释执行。

#### 2.3 编译失败处理

```csharp
try
{
    if (compiler.TryCompile(expr, out var del))
    {
        cache.Store(expr, del);
        return del(ctx);
    }
}
catch (NotSupportedException)
{
    // 节点不支持编译, 标记为 uncacheable, 后续不再尝试。
    cache.MarkUncacheable(expr);
}
// 回退到解释执行。
return EvaluateExpressionInterpreted(expr);
```

### 3. HotPathTracker

`HotPathTracker` 跟踪每个 AST 节点的调用次数, 识别热点路径。

```csharp
public sealed class HotPathTracker
{
    public void RecordInvocation(Expression expr);     // 每次调用记录
    public int GetInvocationCount(Expression expr);     // 查询调用次数
    public bool IsHotPath(Expression expr);             // 是否达到 Tier 1 阈值
    public bool ShouldOptimize(Expression expr);        // 是否达到 Tier 2 阈值
    public void Reset(Expression expr);                 // 编译后重置 (避免重复编译)
}
```

#### 3.1 阈值配置

| 阈值 | 默认值 | 含义 |
|---|---|---|
| `HotPathThreshold` | 32 | 达到此次数触发 Tier 1 编译 |
| `OptimizationThreshold` | 1024 | 达到此次数触发 Tier 2 编译 (预留) |
| `TrackingWindowMs` | 60000 | 1 分钟滑动窗口, 超出窗口的计数衰减 |

**滑动窗口策略**: 每 60 秒对所有计数器做一次衰减 (×0.5), 避免长期未访问的代码占满缓存。

#### 3.2 并发安全

- `HotPathTracker` 内部用 `ConcurrentDictionary<Expression, InvocationRecord>` 存储计数
- `InvocationRecord.Count` 用 `Interlocked.Increment` 原子递增
- `LastInvocation` 时间戳用于窗口判定

### 4. ICompilationCache

```csharp
public interface ICompilationCache
{
    bool TryGet(Expression expr, out Func<ExecutionContext, object?> del);
    void Store(Expression expr, Func<ExecutionContext, object?> del);
    void MarkUncacheable(Expression expr);
    bool IsUncacheable(Expression expr);
    void Clear();
    CompilationCacheStats GetStats();
}

public readonly record struct CompilationCacheStats(
    int CacheEntries,
    int UncacheableEntries,
    long CacheHits,
    long CacheMisses,
    long CompilationAttempts,
    long CompilationFailures);
```

#### 4.1 InMemoryCompilationCache 默认实现

- **存储**: `ConcurrentDictionary<Expression, CacheEntry>`, key 为 AST 节点 (Expression 是 record, 值相等比较)
- **容量上限**: 默认 1024 条目, 超出时按 LRU (最近最少使用) 淘汰
- **Uncacheable 集合**: `HashSet<Expression>` (加锁), 避免重复尝试编译已知不支持的节点
- **统计**: 原子计数 hits / misses / compilation attempts / failures

#### 4.2 缓存 key 语义

Expression 是 `record` 类型, 默认基于字段值做相等比较。同一源代码位置解析出的两个 AST 实例 (如 REPL 重复求值) **算同一个 key**, 复用编译结果。

**例外**: `ScriptBlockExpression` 含 `SourceText` / `SourceFile`, 不同源文件的脚本块即使语句相同也算不同 key (避免闭包捕获错误)。

### 5. Evaluator 集成

`Evaluator.EvaluateExpression` 在原 AST switch 前增加缓存查询:

```csharp
public ExecutionResult EvaluateExpression(Expression expr)
{
    // ADR-0058 §5: JIT 委托缓存查询。
    var cache = _ctx.Host?.Services?.GetService<ICompilationCache>();
    var tracker = _ctx.Host?.Services?.GetService<HotPathTracker>();
    if (cache is not null && tracker is not null)
    {
        // 已编译: 直接调用委托。
        if (cache.TryGet(expr, out var del))
            return ExecutionResult.Of(del(_ctx));

        // 已标记 uncacheable: 跳过编译, 走解释。
        if (cache.IsUncacheable(expr))
            return EvaluateExpressionInterpreted(expr);

        // 记录调用, 达阈值则尝试编译。
        tracker.RecordInvocation(expr);
        if (tracker.IsHotPath(expr))
        {
            var compiler = _ctx.Host?.Services?.GetService<ExpressionCompiler>();
            if (compiler is not null)
            {
                try
                {
                    if (compiler.TryCompile(expr, out del))
                    {
                        cache.Store(expr, del);
                        return ExecutionResult.Of(del(_ctx));
                    }
                }
                catch (NotSupportedException)
                {
                    cache.MarkUncacheable(expr);
                }
            }
        }
    }

    return EvaluateExpressionInterpreted(expr);
}
```

**注**: `EvaluateExpressionInterpreted` 是原 `EvaluateExpression` 的 switch 主体 (重命名), 语义不变。

### 6. ExpressionCompiler 实现细节

#### 6.1 字面量编译

```csharp
private static Func<ExecutionContext, object?> CompileLiteral(LiteralExpression l)
    => _ => l.Value;
```

#### 6.2 变量编译

```csharp
private static Func<ExecutionContext, object?> CompileVariable(VariableExpression v)
{
    var (name, scope) = (v.Name, v.Scope);
    return ctx => ctx.Variables?.Get(name, scope);
}
```

#### 6.3 二元运算编译

```csharp
private Func<ExecutionContext, object?> CompileBinary(BinaryExpression b)
{
    var left = Compile(b.Left);
    var right = Compile(b.Right);
    var op = b.Operator;
    return ctx =>
    {
        var l = left(ctx);
        var r = right(ctx);
        return ApplyBinaryOperator(op, l, r);  // 复用 Evaluator.EvaluateBinary 逻辑
    };
}
```

**关键**: `ApplyBinaryOperator` 复用 Evaluator 已有逻辑 (类型检查 / 隐式转换 / PowerShell 比较语义), 保证编译执行与解释执行结果一致。

#### 6.4 成员访问编译

```csharp
private Func<ExecutionContext, object?> CompileMember(MemberExpression m)
{
    var target = Compile(m.Target);
    var args = m.Arguments?.Select(Compile).ToArray();
    var name = m.MemberName;
    var isStatic = m.Static;
    return ctx =>
    {
        var t = target(ctx);
        var argValues = args?.Select(a => a(ctx)).ToArray();
        return ResolveMember(t, name, isStatic, argValues);  // 复用反射逻辑
    };
}
```

### 7. DI 扩展

```csharp
public static class CompilationServiceCollectionExtensions
{
    public static IServiceCollection AddJitCompilation(this IServiceCollection services)
    {
        services.AddSingleton<ICompilationCache, InMemoryCompilationCache>();
        services.AddSingleton<HotPathTracker>();
        services.AddSingleton<ExpressionCompiler>();
        return services;
    }
}
```

- 所有组件注册为 **Singleton**: 缓存 / 计数器 / 编译器在进程内共享, 跨 ExecutionContext 复用
- 不注册 `IHostedService`: JIT 编译器无后台任务, 按需触发

### 8. 安全与一致性

1. **语义不变**: 编译执行与解释执行对相同 AST 必须产生相同结果 (含异常类型 / 错误消息 / FlowSignalKind), 单元测试需覆盖编译路径与解释路径的对照
2. **不缓存未捕获的 ScriptBlock 闭包**: 编译委托捕获 `ExecutionContext` 引用, 但 `Variables` / `Commands` 等是运行时状态, 不能跨调用缓存
3. **失败保守**: 编译过程中任何异常 (Reflection / 类型检查) 都视为 NotSupportedException, 回退到解释执行
4. **不绕过 ADR-0036 沙箱**: 编译委托仍调用 `ICommandRegistry.Resolve` 等运行时服务, 沙箱拦截照常生效
5. **不绕过 ADR-0054 ExecutionPolicy**: ScriptBlock 编译不触发文件加载, ExecutionPolicy 检查仍由 `Evaluator.EvaluateUsing` 把关

## Costs

1. **内存开销**: 每个编译委托约 200-500 字节 (闭包捕获), 1024 条目上限下约 0.5MB
2. **首次编译延迟**: 单表达式编译约 0.1-1ms (反射 + 委托构造), 热点路径 32 次调用后摊销
3. **复杂度增加**: Evaluator.EvaluateExpression 多一层缓存查询分支, 非热点路径增加约 50-100ns 开销 (可接受)
4. **维护成本**: 编译路径与解释路径需保持语义一致, AST 节点新增时需同步更新 ExpressionCompiler

## Alternatives Considered

### A. 全量 IL emit (Expression Trees / DynamicMethod)

**拒绝**: IL emit 复杂度高, 需为每个 AST 节点类型生成 IL 指令, 调试困难, 且 .NET ExpressionTree 不支持所有 C# 语义 (如 ref / out / async)。本 ADR 优先用委托组合, IL emit 留给 Tier 2 未来 ADR。

### B. Roslyn 编译 (.osh → C# → DLL)

**拒绝**: 编译延迟过大 (Roslyn 首次编译秒级), 内存占用高 (每次编译加载整个 Roslyn), 不适合 REPL 场景。且 OpenShell 语义与 C# 不完全对齐 (如 `$using:` / `$matches` 副作用)。

### C. 仅缓存 AST 解析结果 (不编译)

**拒绝**: AST 解析缓存 (ADR-0041 已部分实现) 只解决解析开销, 不解决执行开销。本 ADR 解决的是执行路径的 dispatch 成本。

### D. 全量预编译 (启动时编译所有脚本块)

**拒绝**: 启动延迟不可接受 (profile 脚本可能含大量未使用的函数), 且大部分代码冷路径无需编译。

## Open Questions

1. **Tier 2 IL emit 时机**: 是否在 Tier 1 委托调用次数达 1024 时异步编译 IL? 异步编译期间用 Tier 1 还是 Tier 0? (建议: 用 Tier 1, 编译完成后原子替换)
2. **类型特化**: `BinaryExpression(Add)` 在 int + int 场景可特化为 `(int l, int r) => l + r`, 避免每次类型检查。是否在 Tier 2 实现? (建议: 是, 但需类型稳定性分析)
3. **ScriptBlockAst 整体编译**: 当前仅编译单表达式, 脚本块体 (多语句 + 控制流) 的编译需独立 ADR (含 FlowSignalKind 传播)
4. **跨 ExecutionContext 委托复用**: 同一 AST 在不同 ExecutionContext (如多线程 pipeline) 下复用委托是否安全? (建议: 是, 委托仅捕获 AST 不捕获 ctx, ctx 作为参数传入)

## References

- [V8 Engine: Tiered Compilation](https://v8.dev/blog/turbofan)
- [.NET RyuJIT: Tiered Compilation](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-core-2-1#tiered-compilation)
- ADR-0045 (Control Flow Statements)
- ADR-0046 (Script Blocks)
- ADR-0047 (Variable System Runtime)
- ADR-0050 (Modern Syntax)
- ADR-0051 (Async/Await)
