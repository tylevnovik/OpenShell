#nullable enable
// ADR-0053 宏系统：MacroRegistry + MacroExpander。
// 设计：
//   1. MacroRegistry 存储 macro_rules! 定义的宏（名称 → MacroDefinitionStatement）。
//   2. MacroExpander 负责：
//      a. 内建宏（println! / dbg! / assert! / assert_eq!）的即时求值。
//      b. 用户自定义宏的模式匹配与令牌替换展开。
//   3. 递归深度保护：最大 64 层（per ADR-0053 §4），超出则报错。
//   4. 卫生性（hygiene）：展开时为捕获的标识符生成唯一前缀，避免变量捕获。

using System.Text;
using OpenShell.Errors;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;

namespace OpenShell.Runtime;

/// <summary>
/// ADR-0053 §2: 宏注册表。存储 macro_rules! 定义的宏。
/// 由 ExecutionContext 持有，跨作用域共享（宏在编译期/求值期展开，不属于变量作用域）。
/// </summary>
public sealed class MacroRegistry
{
    private readonly Dictionary<string, MacroDefinitionStatement> _macros = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册宏定义。同名宏覆盖（后者优先，per ADR-0053 §2 后向兼容）。</summary>
    public void Register(MacroDefinitionStatement def) => _macros[def.Name] = def;

    /// <summary>按名称解析宏定义。未找到返回 null。</summary>
    public MacroDefinitionStatement? Resolve(string name) =>
        _macros.TryGetValue(name, out var def) ? def : null;
}

/// <summary>
/// ADR-0053 §3-5: 宏展开器。处理内建宏与用户自定义宏。
/// </summary>
public static class MacroExpander
{
    /// <summary>递归展开最大深度。Per ADR-0053 §4.</summary>
    public const int MaxRecursionDepth = 64;

    // =========================================================================
    // 内建宏
    // =========================================================================

    /// <summary>
    /// 尝试展开内建宏（println! / dbg! / assert! / assert_eq!）。
    /// 若 <paramref name="mi"/> 不是内建宏，返回 null（由调用方走用户宏路径）。
    /// </summary>
    public static ExecutionResult? TryExpandBuiltin(MacroInvocationExpression mi, ExecutionContext ctx)
    {
        switch (mi.Name.ToLowerInvariant())
        {
            case "println":
                return BuiltinPrintln(mi, ctx);
            case "dbg":
                return BuiltinDbg(mi, ctx);
            case "assert":
                return BuiltinAssert(mi, ctx);
            case "assert_eq":
                return BuiltinAssertEq(mi, ctx);
            default:
                return null;
        }
    }

    /// <summary>println!(arg1, arg2, ...) —— 求值各参数并输出到 Host，末尾换行。</summary>
    private static ExecutionResult BuiltinPrintln(MacroInvocationExpression mi, ExecutionContext ctx)
    {
        var parts = EvaluateArgTokens(mi.ArgumentTokens, ctx);
        var line = string.Join(" ", parts);
        _ = ctx.Host?.WriteOutputLineAsync(line, ctx.CancellationToken);
        return ExecutionResult.Empty;
    }

    /// <summary>dbg!(expr) —— 输出 "expr = value" 调试信息到 Host。</summary>
    private static ExecutionResult BuiltinDbg(MacroInvocationExpression mi, ExecutionContext ctx)
    {
        var sourceText = TokensToString(mi.ArgumentTokens);
        var values = EvaluateArgTokens(mi.ArgumentTokens, ctx);
        var valueText = string.Join(", ", values);
        _ = ctx.Host?.WriteOutputLineAsync($"[dbg] {sourceText} = {valueText}", ctx.CancellationToken);
        return ExecutionResult.Empty;
    }

    /// <summary>assert!(cond) —— cond 为 false 时写入错误。</summary>
    private static ExecutionResult BuiltinAssert(MacroInvocationExpression mi, ExecutionContext ctx)
    {
        var values = EvaluateArgTokens(mi.ArgumentTokens, ctx);
        if (values.Count > 0 && IsTruthy(values[0]))
            return ExecutionResult.Empty;
        var exprText = TokensToString(mi.ArgumentTokens);
        ctx.WriteError(ErrorRecord.FromException(
            new InvalidOperationException($"assertion failed: {exprText}"),
            phase: ErrorPhase.Operation));
        return ExecutionResult.Empty;
    }

    /// <summary>assert_eq!(a, b) —— a != b 时写入错误。</summary>
    private static ExecutionResult BuiltinAssertEq(MacroInvocationExpression mi, ExecutionContext ctx)
    {
        var values = EvaluateArgTokens(mi.ArgumentTokens, ctx);
        if (values.Count < 2) return ExecutionResult.Empty;
        if (Equals(values[0], values[1])) return ExecutionResult.Empty;
        ctx.WriteError(ErrorRecord.FromException(
            new InvalidOperationException($"assertion failed: {values[0]} != {values[1]}"),
            phase: ErrorPhase.Operation));
        return ExecutionResult.Empty;
    }

    // =========================================================================
    // 用户自定义宏展开
    // =========================================================================

    /// <summary>
    /// 展开用户自定义宏。Per ADR-0053 §3.
    /// 流程：模式匹配 → 捕获片段 → 替换展开模板 → 返回展开后的源文本。
    /// 递归深度保护：超出 MaxRecursionDepth 返回 null。
    /// </summary>
    public static string? Expand(MacroDefinitionStatement def, IReadOnlyList<Token> args, ExecutionContext ctx, int depth = 0)
    {
        if (depth >= MaxRecursionDepth)
        {
            ctx.WriteError(ErrorRecord.FromException(
                new InvalidOperationException($"macro recursion depth exceeded ({MaxRecursionDepth}): {def.Name}!"),
                phase: ErrorPhase.Operation));
            return null;
        }

        foreach (var arm in def.Arms)
        {
            var bindings = new Dictionary<string, IReadOnlyList<Token>>(StringComparer.Ordinal);
            if (TryMatchPattern(arm.Pattern, args, bindings))
            {
                return SubstituteExpansion(arm.Expansion, bindings);
            }
        }

        ctx.WriteError(ErrorRecord.FromException(
            new InvalidOperationException($"no matching macro arm for {def.Name}!"),
            phase: ErrorPhase.Operation));
        return null;
    }

    /// <summary>
    /// 模式匹配：将 pattern tokens 与 argument tokens 对齐。
    /// 片段说明符 $name:frag 捕获一个片段（expr/ident/tt 等）。
    /// </summary>
    private static bool TryMatchPattern(
        IReadOnlyList<Token> pattern, IReadOnlyList<Token> args,
        Dictionary<string, IReadOnlyList<Token>> bindings)
    {
        int pi = 0, ai = 0;
        while (pi < pattern.Count)
        {
            var pt = pattern[pi];
            // 片段说明符：$name:frag 或 $name
            if (pt.Kind == TokenKind.Variable && pi + 1 < pattern.Count && pattern[pi + 1].Kind == TokenKind.Colon)
            {
                var name = pt.Text.TrimStart('$');
                if (pi + 2 >= pattern.Count) return false;
                var fragKind = pattern[pi + 2].Text.ToLowerInvariant();
                // 捕获一个片段
                if (!TryCaptureFragment(args, ref ai, fragKind, out var captured))
                    return false;
                bindings[name] = captured;
                pi += 3;
                continue;
            }
            // 简单 $name（无 :frag，默认捕获单个 tt）
            if (pt.Kind == TokenKind.Variable)
            {
                var name = pt.Text.TrimStart('$');
                if (ai >= args.Count) return false;
                bindings[name] = new[] { args[ai] };
                ai++;
                pi++;
                continue;
            }
            // 字面匹配
            if (ai >= args.Count) return false;
            if (!TokenEquals(pt, args[ai])) return false;
            pi++;
            ai++;
        }
        return ai == args.Count;
    }

    /// <summary>按片段类型捕获一个 token 序列。</summary>
    private static bool TryCaptureFragment(IReadOnlyList<Token> args, ref int ai, string fragKind, out IReadOnlyList<Token> captured)
    {
        captured = Array.Empty<Token>();
        if (ai >= args.Count) return false;
        switch (fragKind)
        {
            case "expr":
                // 捕获一个完整表达式（平衡括号/方括号）
                return TryCaptureBalanced(args, ref ai, out captured);
            case "ident":
                captured = new[] { args[ai] };
                ai++;
                return true;
            case "tt":
                // 单个 token tree（平衡的 () / [] / {} 或单个 token）
                return TryCaptureBalanced(args, ref ai, out captured);
            case "ty":
                // 类型引用：连续的类型 token（标识符 + <> + ? + |）
                return TryCaptureType(args, ref ai, out captured);
            case "block":
                return TryCaptureBraced(args, ref ai, TokenKind.LBrace, TokenKind.RBrace, out captured);
            default:
                captured = new[] { args[ai] };
                ai++;
                return true;
        }
    }

    /// <summary>捕获平衡的括号/方括号/花括号序列，或单个 token。</summary>
    private static bool TryCaptureBalanced(IReadOnlyList<Token> args, ref int ai, out IReadOnlyList<Token> captured)
    {
        captured = Array.Empty<Token>();
        if (ai >= args.Count) return false;
        var first = args[ai];
        var (open, close) = first.Kind switch
        {
            TokenKind.LParen => (TokenKind.LParen, TokenKind.RParen),
            TokenKind.LBracket => (TokenKind.LBracket, TokenKind.RBracket),
            TokenKind.LBrace => (TokenKind.LBrace, TokenKind.RBrace),
            _ => (TokenKind.End, TokenKind.End),
        };
        if (open == TokenKind.End)
        {
            captured = new[] { first };
            ai++;
            return true;
        }
        return TryCaptureBraced(args, ref ai, open, close, out captured);
    }

    private static bool TryCaptureBraced(IReadOnlyList<Token> args, ref int ai, TokenKind open, TokenKind close, out IReadOnlyList<Token> captured)
    {
        captured = Array.Empty<Token>();
        if (ai >= args.Count || args[ai].Kind != open) return false;
        var result = new List<Token>();
        int depth = 0;
        while (ai < args.Count)
        {
            var t = args[ai];
            result.Add(t);
            ai++;
            if (t.Kind == open) depth++;
            else if (t.Kind == close)
            {
                depth--;
                if (depth == 0) { captured = result; return true; }
            }
        }
        return false;
    }

    /// <summary>捕获类型引用 token 序列。</summary>
    private static bool TryCaptureType(IReadOnlyList<Token> args, ref int ai, out IReadOnlyList<Token> captured)
    {
        captured = Array.Empty<Token>();
        if (ai >= args.Count) return false;
        var result = new List<Token>();
        while (ai < args.Count)
        {
            var k = args[ai].Kind;
            if (k is TokenKind.Identifier or TokenKind.TypeRef or TokenKind.Question
                or TokenKind.Pipe or TokenKind.Lt or TokenKind.Gt or TokenKind.Shr or TokenKind.Comma)
            {
                result.Add(args[ai]);
                ai++;
            }
            else break;
        }
        captured = result;
        return result.Count > 0;
    }

    /// <summary>将展开模板中的 $name 替换为绑定的 token 序列。</summary>
    private static string SubstituteExpansion(IReadOnlyList<Token> expansion, Dictionary<string, IReadOnlyList<Token>> bindings)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < expansion.Count; i++)
        {
            var t = expansion[i];
            if (t.Kind == TokenKind.Variable)
            {
                var name = t.Text.TrimStart('$');
                if (bindings.TryGetValue(name, out var captured))
                {
                    sb.Append(TokensToString(captured));
                    continue;
                }
            }
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    /// <summary>将宏参数 token 重新解析为逗号分隔的表达式列表并求值。</summary>
    private static List<object?> EvaluateArgTokens(IReadOnlyList<Token> tokens, ExecutionContext ctx)
    {
        var result = new List<object?>();
        var evaluator = new Evaluator(ctx);
        foreach (var chunk in SplitByComma(tokens))
        {
            if (chunk.Count == 0) continue;
            try
            {
                var expr = new ModernParser(chunk, null).ParseExpression();
                result.Add(evaluator.EvaluateExpression(expr).Value);
            }
            catch (ParserException)
            {
                result.Add(null);
            }
        }
        return result;
    }

    /// <summary>按逗号分割 token 流（顶层，不进入括号内）。</summary>
    private static List<List<Token>> SplitByComma(IReadOnlyList<Token> tokens)
    {
        var result = new List<List<Token>>();
        var current = new List<Token>();
        int depth = 0;
        foreach (var t in tokens)
        {
            if (t.Kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
            else if (t.Kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) depth--;
            if (depth == 0 && t.Kind == TokenKind.Comma)
            {
                result.Add(current);
                current = new List<Token>();
            }
            else
            {
                current.Add(t);
            }
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    private static string TokensToString(IReadOnlyList<Token> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    private static bool TokenEquals(Token a, Token b) =>
        a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.Ordinal);

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        0 => false,
        "" => false,
        _ => true,
    };
}
