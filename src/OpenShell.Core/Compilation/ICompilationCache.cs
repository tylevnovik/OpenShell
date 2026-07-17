#nullable enable
// ADR-0058 §1/§4: JIT 编译缓存接口与统计类型。
// 设计：
//   1. ICompilationCache 缓存 Expression → Func<ExecutionContext, object?> 委托。
//   2. Expression 是 record，按字段值做相等比较；同源 AST 实例复用编译结果。
//   3. InMemoryCompilationCache 提供 LRU + 容量上限，避免长进程内存膨胀。
//   4. MarkUncacheable 标记不支持编译的 AST，避免重复尝试。

using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;

namespace OpenShell.Compilation;

/// <summary>JIT 编译层级。Per ADR-0058 §1.</summary>
public enum CompilationTier
{
    /// <summary>Tier 0: 解释执行 (Evaluator AST switch)。</summary>
    Interpreted = 0,

    /// <summary>Tier 1: 委托缓存 (ExpressionCompiler.Compile → Func&lt;ExecutionContext, object?&gt;)。</summary>
    Compiled = 1,

    /// <summary>Tier 2: 优化编译 (IL emit / ExpressionTree, 预留)。</summary>
    Optimized = 2,
}

/// <summary>JIT 编译缓存统计。Per ADR-0058 §4.</summary>
public readonly record struct CompilationCacheStats(
    int CacheEntries,
    int UncacheableEntries,
    long CacheHits,
    long CacheMisses,
    long CompilationAttempts,
    long CompilationFailures);

/// <summary>
/// 表达式编译结果缓存接口。Per ADR-0058 §4.
/// <para>
/// 缓存 Expression (AST 节点) → 编译后的 <see cref="Func{ExecutionContext, Object}"/> 委托。
/// Expression 是 record，按字段值相等比较作为 key，同一源码位置多次解析复用同一委托。
/// </para>
/// </summary>
public interface ICompilationCache
{
    /// <summary>查询已编译的委托。命中返回 true 并赋值 <paramref name="del"/>。</summary>
    bool TryGet(Expression expr, out Func<ExecutionContext, object?> del);

    /// <summary>缓存编译后的委托。Per ADR-0058 §4.1: LRU 淘汰策略。</summary>
    void Store(Expression expr, Func<ExecutionContext, object?> del);

    /// <summary>标记 AST 节点为不可编译（如包含不支持的子节点），后续不再尝试。</summary>
    void MarkUncacheable(Expression expr);

    /// <summary>查询是否已标记为不可编译。</summary>
    bool IsUncacheable(Expression expr);

    /// <summary>清空所有缓存与统计。</summary>
    void Clear();

    /// <summary>获取当前缓存统计（命中数 / 未命中数 / 编译尝试数 / 失败数）。</summary>
    CompilationCacheStats GetStats();
}
