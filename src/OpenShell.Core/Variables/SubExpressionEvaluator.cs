#nullable enable
// ADR-0045 §14 + ADR-0047 §5 子表达式 $(...) / 数组子表达式 @(...) 求值器。
// 接入 PowerShellParser + Evaluator 进行 AST 求值，收集每条语句的输出。
//
// 行为契约 (ADR-0047 §5.1-5.3):
// - $(...)  在当前作用域求值 (不创建新作用域), 返回 0/1/N 个输出 (N=0→null, N=1→原值, N>=2→object[])
// - @(...)  在当前作用域求值, 始终返回 object[] (0/1/N 个输出都包装为数组)
// - 赋值语句不产生输出, 命令 / 表达式语句产生输出 (per ADR-0010 管道对象流)

using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;

using ExecutionContext = OpenShell.Runtime.ExecutionContext;

namespace OpenShell.Variables;

/// <summary>
/// 子表达式 $(...) / 数组子表达式 @(...) 求值器。Per ADR-0047 §5.
/// 将表达式文本解析为 AST 并通过 <see cref="Evaluator"/> 求值, 收集输出项。
/// </summary>
public sealed class SubExpressionEvaluator
{
    /// <summary>占位构造 (供 DI 占位注册)。</summary>
    public SubExpressionEvaluator() { }

    /// <summary>
    /// 求值 $(...) 子表达式。Per ADR-0047 §5.1.
    /// <para>
    /// 在当前作用域求值 (不创建新作用域), 返回 0/1/N 个输出:
    /// <list type="bullet">
    ///   <item>0 个输出 → <c>null</c></item>
    ///   <item>1 个输出 → 该值 (原类型)</item>
    ///   <item>2+ 个输出 → <c>object[]</c> 数组</item>
    /// </list>
    /// </para>
    /// <para>
    /// 赋值语句 (<c>$a = 1</c>) 不产生输出 (per ADR-0047 §5.3);
    /// 表达式语句 / 命令调用产生输出。
    /// </para>
    /// </summary>
    /// <param name="expressionText">括号内表达式文本 (如 <c>$arr.Count + 1</c>)。</param>
    /// <param name="variables">变量注册表 (用于构造 ExecutionContext, 命令调用能力受限)。</param>
    /// <returns>求值结果 (0 输出 → null, 1 输出 → 原值, N 输出 → object[])。</returns>
    public object? EvaluateSubExpression(string expressionText, IVariableRegistry variables)
    {
        ArgumentNullException.ThrowIfNull(expressionText);
        ArgumentNullException.ThrowIfNull(variables);

        var outputs = EvaluateCore(expressionText, variables);
        return outputs.Count switch
        {
            0 => null,
            1 => outputs[0],
            _ => outputs.ToArray(),
        };
    }

    /// <summary>
    /// 求值 @(...) 数组子表达式。Per ADR-0047 §5.2.
    /// <para>
    /// 求值流程同 $(...), 但返回值始终为 <c>object[]</c>:
    /// <list type="bullet">
    ///   <item>0 个输出 → 空数组 <c>Array.Empty&lt;object&gt;()</c></item>
    ///   <item>1 个输出 → 单元素数组</item>
    ///   <item>2+ 个输出 → 数组</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="expressionText">括号内表达式文本。</param>
    /// <param name="variables">变量注册表。</param>
    /// <returns>始终返回 object[] (空数组 / 单元素数组 / 多元素数组)。</returns>
    public object[] EvaluateArraySubExpression(string expressionText, IVariableRegistry variables)
    {
        ArgumentNullException.ThrowIfNull(expressionText);
        ArgumentNullException.ThrowIfNull(variables);

        var outputs = EvaluateCore(expressionText, variables);
        if (outputs.Count == 0) return Array.Empty<object>();
        return outputs.ToArray();
    }

    /// <summary>
    /// 核心求值逻辑：解析 → 构造上下文 → 逐语句求值 → 收集输出 (排除赋值语句)。
    /// </summary>
    private static List<object> EvaluateCore(string expressionText, IVariableRegistry variables)
    {
        // 解析表达式文本为 AST (per ADR-0045 §14 PowerShellParser)。
        var ast = PowerShellParser.Parse(expressionText);

        // 在当前作用域求值 (不创建新作用域, per ADR-0047 §5.1)。
        // 仅传入 variables: 命令调用能力受限, 适合字符串插值场景 ("count: $($arr.Count + 1)")。
        var ctx = new ExecutionContext(variables);
        var evaluator = new Evaluator(ctx);

        var outputs = new List<object>();
        foreach (var stmt in ast.Statements)
        {
            var result = evaluator.EvaluateStatement(stmt);

            // 控制流信号传播: throw 转 OpenShellScriptException; break/continue/return/exit 终止求值。
            if (result.Signal == FlowSignalKind.Throw)
                throw new OpenShellScriptException(result.ThrownValue, ctx);
            if (result.Signal != FlowSignalKind.None)
                break;

            // Per ADR-0047 §5.3: 赋值语句不产生输出 (左值不流出)。
            // Parser 把 $a = 1 解析为 ExpressionStatement(AssignmentExpression) (ParseExpression 先消费 =),
            // 顶层 AssignmentStatement 形式罕见但同样不应产生输出。
            if (stmt is AssignmentStatement) continue;
            if (stmt is ExpressionStatement es && es.Expression is AssignmentExpression) continue;

            // 表达式语句 / 命令调用 / 控制流块产生的输出。
            // null 值不作为输出 (避免 [int]$x = $null 产生 null 流)。
            if (result.Value is { } value)
                outputs.Add(value);
        }

        return outputs;
    }
}
