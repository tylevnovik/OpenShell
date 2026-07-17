#nullable enable
// ADR-0058 §7: JIT 编译 DI 注册扩展。

using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Compilation;

/// <summary>
/// ADR-0058 JIT 编译 DI 注册扩展。
/// 在 <c>Program.cs</c> 的 <c>ConfigureServices</c> 中调用 <see cref="AddJitCompilation"/> 一次,
/// 注册 <see cref="ICompilationCache"/> / <see cref="HotPathTracker"/> / <see cref="ExpressionCompiler"/> 为单例。
/// </summary>
public static class CompilationServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0058 JIT 编译服务:
    /// <list type="bullet">
    ///   <item><see cref="ICompilationCache"/> → <see cref="InMemoryCompilationCache"/> (singleton, LRU 1024 条目)。</item>
    ///   <item><see cref="HotPathTracker"/> (singleton, 调用计数 + 滑动窗口衰减)。</item>
    ///   <item><see cref="ExpressionCompiler"/> (singleton, AST → Func&lt;ExecutionContext, object?&gt; 编译器)。</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddJitCompilation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICompilationCache, InMemoryCompilationCache>();
        services.AddSingleton<HotPathTracker>();
        services.AddSingleton<ExpressionCompiler>();
        return services;
    }
}
