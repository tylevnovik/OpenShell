#nullable enable
// ADR-0058 §2/§6: AST → Func<ExecutionContext, object?> 委托编译器。
// 设计：
//   1. 仅编译纯表达式节点 (Literal / Variable / Binary / Unary / Member / Index / Cast / Sub / Array / Range / Ternary)。
//   2. 不支持的节点抛 NotSupportedException, 由 Evaluator 捕获并回退到解释执行。
//   3. 编译委托接收 ExecutionContext 作为参数, 复用 Evaluator 的 public static 辅助方法 (Add / Subtract / IsTruthy / GetMember / GetIndex / InvokeMethod / ResolveType / ConvertValue) 保证语义一致。
//   4. 含控制流信号 (return / break / continue / throw) 的节点不编译 (PipelineExpression / CommandExpression / ScriptBlockExpression / AssignmentExpression / LambdaExpression / MatchExpression / HashExpression / AwaitExpressionAst / AsyncBlockExpression)。

using System.Collections;
using System.Reflection;
using OpenShell.Items;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;
using OpenShell.Variables;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;
using TypeReferenceExpression = OpenShell.Parsing.Ast.TypeReferenceExpression;

namespace OpenShell.Compilation;

/// <summary>
/// AST 表达式编译器：将 <see cref="Expression"/> 编译为
/// <see cref="Func{ExecutionContext, Object}"/> 委托以跳过 AST dispatch 开销。Per ADR-0058 §2.
/// <para>
/// 仅支持纯表达式节点; 不支持的节点抛 <see cref="NotSupportedException"/>, 由 Evaluator 回退到解释执行。
/// 编译委托复用 <see cref="Evaluator"/> 的 public static 辅助方法 (Add / Subtract / IsTruthy / GetMember / GetIndex / InvokeMethod / ResolveType / ConvertValue),
/// 保证编译执行与解释执行结果完全一致。
/// </para>
/// </summary>
public sealed class ExpressionCompiler
{
    /// <summary>
    /// 尝试编译表达式为委托。Per ADR-0058 §2.
    /// <para>成功返回 true 并赋值 <paramref name="del"/>; 失败 (节点不支持) 抛 <see cref="NotSupportedException"/>。</para>
    /// </summary>
    public bool TryCompile(Expression expr, out Func<ExecutionContext, object?> del)
    {
        del = Compile(expr);
        return del is not null;
    }

    /// <summary>编译表达式。不支持的节点抛 NotSupportedException。</summary>
    public Func<ExecutionContext, object?> Compile(Expression expr) => expr switch
    {
        LiteralExpression l => CompileLiteral(l),
        VariableExpression v => CompileVariable(v),
        BinaryExpression b => CompileBinary(b),
        UnaryExpression u => CompileUnary(u),
        MemberExpression m => CompileMember(m),
        IndexExpression i => CompileIndex(i),
        CastExpression c => CompileCast(c),
        SubExpressionExpression s => Compile(s.Inner),
        ArrayExpression a => CompileArray(a),
        RangeExpression r => CompileRange(r),
        TernaryExpression t => CompileTernary(t),
        // 不支持的节点 (含语句级副作用 / 控制流信号 / 复杂字面量)。
        PipelineExpression => throw Unsupported(expr),
        CommandExpression => throw Unsupported(expr),
        ScriptBlockExpression => throw Unsupported(expr),
        AssignmentExpression => throw Unsupported(expr),
        LambdaExpression => throw Unsupported(expr),
        MatchExpression => throw Unsupported(expr),
        HashExpression => throw Unsupported(expr),
        AwaitExpressionAst => throw Unsupported(expr),
        AsyncBlockExpression => throw Unsupported(expr),
        _ => throw Unsupported(expr),
    };

    // =========================================================================
    // 字面量
    // =========================================================================

    private static Func<ExecutionContext, object?> CompileLiteral(LiteralExpression l)
        => _ => l.Value;

    // =========================================================================
    // 变量
    // =========================================================================

    private static Func<ExecutionContext, object?> CompileVariable(VariableExpression v)
    {
        var name = v.Name;
        var scope = v.Scope;

        // $_ / $PSItem: pipeline 当前项。Per ADR-0042 §3.4.
        if (name == "_" || name.Equals("PSItem", StringComparison.OrdinalIgnoreCase))
        {
            return ctx => ctx.CurrentItem is null ? null : Evaluator.ItemToValuePublic(ctx.CurrentItem);
        }

        // $args: 当前函数/脚本块的位置参数数组。Per ADR-0042 §3.
        if (name.Equals("args", StringComparison.OrdinalIgnoreCase))
        {
            return ctx => ctx.CurrentArgs ?? Array.Empty<object?>();
        }

        return ctx =>
        {
            if (ctx.Variables is null) return null;
            return scope switch
            {
                VariableScopeKind.Environment => Environment.GetEnvironmentVariable(name),
                VariableScopeKind.Global => ctx.Variables.Resolve(name, VariableScope.Global),
                VariableScopeKind.Script => ctx.Variables.Resolve(name, VariableScope.Script),
                VariableScopeKind.Local => ctx.Variables.Resolve(name, VariableScope.Local),
                VariableScopeKind.Private => ctx.Variables.Resolve(name, VariableScope.Private),
                // Per ADR-0047 §1.2 + ADR-0046 §4: $using: 退化为 Local 查找。
                VariableScopeKind.Using => ctx.Variables.Resolve(name, VariableScope.Local),
                _ => ctx.Variables.Resolve(name),
            };
        };
    }

    // =========================================================================
    // 二元运算
    // =========================================================================

    private Func<ExecutionContext, object?> CompileBinary(BinaryExpression b)
    {
        var op = b.Operator;

        // Match / NotMatch 涉及 $matches 自动变量副作用, 不编译 (回退到解释执行)。
        if (op == BinaryOperator.Match || op == BinaryOperator.NotMatch)
            throw Unsupported(b);

        // 短路逻辑 And / Or: 仅在左侧为真/假时求值右侧。
        if (op == BinaryOperator.And)
        {
            var left = Compile(b.Left);
            var right = Compile(b.Right);
            return ctx =>
            {
                var lv = left(ctx);
                if (!Evaluator.IsTruthy(lv)) return false;
                return Evaluator.IsTruthy(right(ctx));
            };
        }
        if (op == BinaryOperator.Or)
        {
            var left = Compile(b.Left);
            var right = Compile(b.Right);
            return ctx =>
            {
                var lv = left(ctx);
                if (Evaluator.IsTruthy(lv)) return true;
                return Evaluator.IsTruthy(right(ctx));
            };
        }
        if (op == BinaryOperator.NullCoalesce)
        {
            var left = Compile(b.Left);
            var right = Compile(b.Right);
            return ctx => left(ctx) ?? right(ctx);
        }

        // 非短路运算: 两边都求值。
        var leftDel = Compile(b.Left);
        var rightDel = Compile(b.Right);
        return ctx =>
        {
            var lv = leftDel(ctx);
            var rv = rightDel(ctx);
            return op switch
            {
                BinaryOperator.Add => Evaluator.Add(lv, rv),
                BinaryOperator.Subtract => Evaluator.Subtract(lv, rv),
                BinaryOperator.Multiply => Evaluator.Multiply(lv, rv),
                BinaryOperator.Divide => Evaluator.Divide(lv, rv),
                BinaryOperator.Modulo => Evaluator.Modulo(lv, rv),
                BinaryOperator.Power => Evaluator.Power(lv, rv),
                BinaryOperator.Eq or BinaryOperator.Equals => Equals(lv, rv),
                BinaryOperator.Ne or BinaryOperator.NotEquals => !Equals(lv, rv),
                BinaryOperator.Lt => Evaluator.CompareValues(lv, rv) < 0,
                BinaryOperator.Gt => Evaluator.CompareValues(lv, rv) > 0,
                BinaryOperator.Le => Evaluator.CompareValues(lv, rv) <= 0,
                BinaryOperator.Ge => Evaluator.CompareValues(lv, rv) >= 0,
                BinaryOperator.Like => Evaluator.LikeMatch(lv, rv, caseSensitive: false),
                BinaryOperator.NotLike => !Evaluator.LikeMatch(lv, rv, caseSensitive: false),
                BinaryOperator.In => Evaluator.InMatchPublic(lv, rv),
                BinaryOperator.NotIn => !Evaluator.InMatchPublic(lv, rv),
                BinaryOperator.Contains => Evaluator.InMatchPublic(rv, lv),
                BinaryOperator.NotContains => !Evaluator.InMatchPublic(rv, lv),
                BinaryOperator.Is => Evaluator.IsTypePublic(lv, rv),
                BinaryOperator.IsNot => !Evaluator.IsTypePublic(lv, rv),
                BinaryOperator.As => Evaluator.ConvertAsPublic(lv, rv),
                BinaryOperator.BitwiseAnd => Evaluator.ToLongPublic(lv) & Evaluator.ToLongPublic(rv),
                BinaryOperator.BitwiseOr => Evaluator.ToLongPublic(lv) | Evaluator.ToLongPublic(rv),
                BinaryOperator.BitwiseXor => Evaluator.ToLongPublic(lv) ^ Evaluator.ToLongPublic(rv),
                BinaryOperator.ShiftLeft => Evaluator.ToLongPublic(lv) << (int)Evaluator.ToLongPublic(rv),
                BinaryOperator.ShiftRight => Evaluator.ToLongPublic(lv) >> (int)Evaluator.ToLongPublic(rv),
                _ => null,
            };
        };
    }

    // =========================================================================
    // 一元运算
    // =========================================================================

    private Func<ExecutionContext, object?> CompileUnary(UnaryExpression u)
    {
        var op = u.Operator;

        // Postfix ++/-- 有副作用 (修改变量), 但语义明确, 可编译。
        if (op == UnaryOperator.PostfixIncrement || op == UnaryOperator.PostfixDecrement)
        {
            if (u.Operand is not VariableExpression ve)
                throw Unsupported(u);
            var name = ve.Name;
            var delta = op == UnaryOperator.PostfixIncrement ? 1L : -1L;
            return ctx =>
            {
                if (ctx.Variables is null) return null;
                var cur = ctx.Variables.Resolve(name);
                var newVal = delta > 0 ? Evaluator.Add(cur, 1L) : Evaluator.Subtract(cur, 1L);
                ctx.Variables.Set(name, newVal!);
                return cur;
            };
        }

        // Prefix ++/-- 同样有副作用。
        if (op == UnaryOperator.PrefixIncrement || op == UnaryOperator.PrefixDecrement)
        {
            if (u.Operand is not VariableExpression ve2)
                throw Unsupported(u);
            var name = ve2.Name;
            var delta = op == UnaryOperator.PrefixIncrement ? 1L : -1L;
            return ctx =>
            {
                if (ctx.Variables is null) return null;
                var cur = ctx.Variables.Resolve(name);
                var newVal = delta > 0 ? Evaluator.Add(cur, 1L) : Evaluator.Subtract(cur, 1L);
                ctx.Variables.Set(name, newVal!);
                return newVal;
            };
        }

        // 无副作用的一元运算。
        var operandDel = Compile(u.Operand);
        return op switch
        {
            UnaryOperator.Not => ctx => !Evaluator.IsTruthy(operandDel(ctx)),
            UnaryOperator.BitwiseNot => ctx => ~Evaluator.ToLongPublic(operandDel(ctx)),
            UnaryOperator.Negate => ctx => Evaluator.Subtract(0L, operandDel(ctx)),
            UnaryOperator.Plus => operandDel,
            UnaryOperator.Spread => operandDel,
            _ => throw Unsupported(u),
        };
    }

    // =========================================================================
    // 成员访问 / 索引 / 类型转换
    // =========================================================================

    private Func<ExecutionContext, object?> CompileMember(MemberExpression m)
    {
        var name = m.MemberName;
        var isStatic = m.Static;
        var nullConditional = m.NullConditional;

        if (isStatic)
        {
            // [Type]::Member —— Target 应为 TypeReferenceExpression。
            if (m.Target is not TypeReferenceExpression te)
                throw Unsupported(m);
            var type = Evaluator.ResolveType(te.Type);
            if (type is null) throw Unsupported(m);

            var hasArgs = m.Arguments is { Count: > 0 };
            var argDels = hasArgs ? m.Arguments!.Select(Compile).ToArray() : null;

            return ctx =>
            {
                if (!hasArgs)
                {
                    return type.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
                        ?.GetValue(null);
                }
                var args = argDels!.Select(d => d(ctx)).ToArray();
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
                    ?.Invoke(null, args);
            };
        }

        // 实例成员访问。
        var targetDel = Compile(m.Target);
        var hasInstanceArgs = m.Arguments is { Count: > 0 };
        var argInstanceDels = hasInstanceArgs ? m.Arguments!.Select(Compile).ToArray() : null;

        return ctx =>
        {
            var target = targetDel(ctx);
            if (target is null) return nullConditional ? null : null;
            if (!hasInstanceArgs)
                return Evaluator.GetMember(target, name);
            var args = argInstanceDels!.Select(d => d(ctx)).ToArray();
            return Evaluator.InvokeMethod(target, name, args);
        };
    }

    private Func<ExecutionContext, object?> CompileIndex(IndexExpression i)
    {
        var targetDel = Compile(i.Target);
        var indexDel = Compile(i.Index);
        return ctx =>
        {
            var target = targetDel(ctx);
            var index = indexDel(ctx);
            return Evaluator.GetIndex(target, index);
        };
    }

    private Func<ExecutionContext, object?> CompileCast(CastExpression c)
    {
        // [ordered]@{ } 含 HashExpression 子节点, 走解释执行 (HashExpression 不支持)。
        var typeName = c.Type.FullName?.ToLowerInvariant();
        if (typeName == "ordered")
            throw Unsupported(c);

        var type = Evaluator.ResolveType(c.Type);
        if (type is null) throw Unsupported(c);

        var operandDel = Compile(c.Operand);
        return ctx =>
        {
            var value = operandDel(ctx);
            return Evaluator.ConvertValue(value, type);
        };
    }

    // =========================================================================
    // 数组 / 范围 / 三元
    // =========================================================================

    private Func<ExecutionContext, object?> CompileArray(ArrayExpression a)
    {
        var elementDels = a.Elements.Select(Compile).ToArray();
        return ctx =>
        {
            var arr = new object?[elementDels.Length];
            for (int i = 0; i < elementDels.Length; i++)
            {
                arr[i] = elementDels[i](ctx);
            }
            return arr;
        };
    }

    private Func<ExecutionContext, object?> CompileRange(RangeExpression r)
    {
        var startDel = Compile(r.Start);
        var endDel = Compile(r.End);
        return ctx =>
        {
            var start = startDel(ctx);
            var end = endDel(ctx);
            return Evaluator.BuildRange(start, end);
        };
    }

    private Func<ExecutionContext, object?> CompileTernary(TernaryExpression t)
    {
        var condDel = Compile(t.Condition);
        var trueDel = Compile(t.IfTrue);
        var falseDel = Compile(t.IfFalse);
        return ctx => Evaluator.IsTruthy(condDel(ctx)) ? trueDel(ctx) : falseDel(ctx);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static NotSupportedException Unsupported(Expression expr)
        => new($"ADR-0058: ExpressionCompiler does not support AST node type '{expr.GetType().Name}'.");
}
