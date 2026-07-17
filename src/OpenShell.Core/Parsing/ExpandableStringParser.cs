#nullable enable
// 可展开字符串解析器。借鉴 PS ScanDollarInStringExpandable + ScanSubExpression
// （tokenizer.cs:2362-2566）+ ExpandableStringExpressionAst（ast.cs:9825-9974）。
// 设计要点（Per ADR-0050 §6.4 + PS 借鉴任务 T-103~T-105）：
//   1. tokenizer 保持上下文不敏感，产出普通 String token（Value 是原始文本）。
//   2. parser 在解析双引号字符串 literal 时调用本类，把原始文本解析为 ExpandableStringExpression。
//   3. 识别 $var / ${name} / $(expr) / $? / $env:NAME / $global:x 等插值段。
//   4. $(expr) 子表达式递归调用 ModernParser 解析（分层延迟解析，借鉴 PS UnscannedSubExprToken）。
//   5. 若无 $ 段，退化为 LiteralExpression(Kind=String/HereString)。
//   6. 求值时由 Evaluator 用 string.Format(FormatExpression, NestedExpressions) 拼接。

using System.Text;
using OpenShell.Parsing.Ast;

namespace OpenShell.Parsing;

/// <summary>
/// 可展开字符串解析器。借鉴 PS ScanDollarInStringExpandable（tokenizer.cs:2518-2566）+
/// ScanSubExpression（tokenizer.cs:2362-2447）。
/// <para>
/// 把双引号字符串原始文本（引号已剥离）解析为 <see cref="ExpandableStringExpression"/>。
/// 支持 $var / ${name} / $(expr) / $? / $env:NAME / $global:x 插值段。
/// 若无插值段，退化为 <see cref="LiteralExpression"/>(Kind=String/HereString)。
/// </para>
/// </summary>
public static class ExpandableStringParser
{
    /// <summary>
    /// 解析双引号字符串原始文本为表达式。
    /// </summary>
    /// <param name="content">原始文本（引号已剥离，backtick 转义已由 tokenizer 处理为裸字符）。</param>
    /// <param name="isHereString">是否 here-string（@"..."@）。</param>
    /// <param name="span">token 的源码位置。</param>
    /// <returns>ExpandableStringExpression（含插值段）或 LiteralExpression（无插值段）。</returns>
    public static Expression Parse(string content, bool isHereString, SourceSpan span)
    {
        if (content.Length == 0 || !content.Contains('$'))
        {
            // 无 $ 字符，不可能有插值段，直接返回字面量。
            return new LiteralExpression(
                content,
                isHereString ? LiteralKind.HereString : LiteralKind.String,
                span);
        }

        var sb = new StringBuilder(content.Length + 16);        // 原始文本重建（含 $ 段原文）
        var formatSb = new StringBuilder(content.Length + 16);  // 格式串（{N} 占位符）
        var nested = new List<Expression>();

        for (int i = 0; i < content.Length; i++)
        {
            var ch = content[i];

            if (ch == '$' && i + 1 < content.Length)
            {
                var next = content[i + 1];
                Expression? nestedExpr = null;
                string originalSegment = string.Empty;

                if (next == '(')
                {
                    // $(expr) 子表达式 —— 借鉴 PS ScanSubExpression（tokenizer.cs:2362-2447）。
                    // 递归扫描括号配对，提取内部表达式文本，用 ModernParser 解析。
                    var (expr, consumed, raw) = ScanSubExpression(content, i + 2, span);
                    if (expr is not null)
                    {
                        nestedExpr = expr;
                        originalSegment = raw; // "$( ... )"
                        i += 1 + consumed; // 跳过 $( ... )，+1 for '$'，consumed 含 '(' 已消费
                        // i 现在指向 ')' 的位置，循环 i++ 会跳过 ')'
                    }
                }
                else if (next == '{')
                {
                    // ${name} 或 ${expr} 形式 —— 找到 } 闭合。Per ADR-0050 §6.4 + T-083.
                    // 若内容为简单变量名（含 scope: 前缀），构造 VariableExpression；
                    // 否则（含运算符/表达式）作为任意表达式用 ModernParser 解析。
                    var close = content.IndexOf('}', i + 2);
                    if (close > i + 1)
                    {
                        var inner = content[(i + 2)..close];
                        if (IsSimpleVariableName(inner))
                        {
                            nestedExpr = BuildVariableExpression(inner, span);
                        }
                        else
                        {
                            // ${expr} 任意表达式插值。Per ADR-0050 §6.4 + T-083.
                            // 用 ModernParser 解析内部文本为表达式。
                            try
                            {
                                var innerAst = ModernParser.Parse(inner);
                                if (innerAst.Statements.Count == 1
                                    && innerAst.Statements[0] is ExpressionStatement es)
                                {
                                    nestedExpr = es.Expression;
                                }
                                else if (innerAst.Statements.Count == 1
                                    && innerAst.Statements[0] is PipelineStatement ps
                                    && ps.Pipeline.Commands.Count == 1
                                    && ps.Pipeline.Commands[0].Arguments.Count == 0)
                                {
                                    // 裸命令表达式（如 ${someCommand}）—— 包装为 CommandExpression。
                                    var cmd = ps.Pipeline.Commands[0];
                                    nestedExpr = new CommandExpression(
                                        cmd.Name, Array.Empty<CommandArgument>(),
                                        CommandInvocationKind.Direct, span);
                                }
                            }
                            catch
                            {
                                // 解析失败：退化为字面变量名（保持向后兼容）。
                                nestedExpr = BuildVariableExpression(inner, span);
                            }
                        }
                        originalSegment = content[i..(close + 1)];
                        i = close; // 循环 i++ 跳过 '}'
                    }
                }
                else if (IsVariableNameFirstChar(next))
                {
                    // $name 形式（含 $env:NAME / $global:x / $?）—— 匹配最长变量名。
                    var (name, end) = ScanVariableName(content, i + 1);
                    if (end > i + 1)
                    {
                        // 检查紧跟的 .Property / [index] 后缀（与变量一起作为嵌套表达式）。
                        var (suffix, suffixEnd) = ScanSuffix(content, end);
                        var fullExpr = BuildVariableWithSuffix(name, suffix, span);
                        nestedExpr = fullExpr;
                        originalSegment = content[i..suffixEnd];
                        i = suffixEnd - 1; // 循环 i++ 跳过最后一个后缀字符
                    }
                }

                if (nestedExpr is not null)
                {
                    sb.Append(originalSegment);
                    formatSb.Append('{').Append(nested.Count).Append('}');
                    nested.Add(nestedExpr);
                    continue;
                }
            }

            // 普通字符：追加到 sb 和 formatSb。
            // {/} 在 formatSb 中需 doubling 为 {{/}}（string.Format 转义）。
            sb.Append(ch);
            if (ch == '{' || ch == '}')
            {
                formatSb.Append(ch);
                formatSb.Append(ch);
            }
            else
            {
                formatSb.Append(ch);
            }
        }

        if (nested.Count == 0)
        {
            // 有 $ 但未匹配到合法插值段，返回字面量。
            return new LiteralExpression(
                content,
                isHereString ? LiteralKind.HereString : LiteralKind.String,
                span);
        }

        return new ExpandableStringExpression(
            Value: sb.ToString(),
            FormatExpression: formatSb.ToString(),
            NestedExpressions: nested,
            IsHereString: isHereString,
            Span: span);
    }

    /// <summary>
    /// 扫描 $(expr) 子表达式。借鉴 PS ScanSubExpression（tokenizer.cs:2362-2447）。
    /// 递归扫描括号配对，处理嵌套字符串/转义，提取内部表达式文本并用 ModernParser 解析。
    /// </summary>
    /// <param name="content">父字符串内容。</param>
    /// <param name="start">子表达式内部起始位置（即 '(' 之后）。</param>
    /// <param name="parentSpan">父 token 的 Span（用于构造子表达式 AST）。</param>
    /// <returns>(解析的表达式, 消费的字符数含 ')', 原始 "$( ... )" 文本)。expr 为 null 表示扫描失败。</returns>
    private static (Expression? expr, int consumed, string raw) ScanSubExpression(
        string content, int start, SourceSpan parentSpan)
    {
        // start 指向 '(' 之后第一个字符。
        int parenCount = 1;
        var sb = new StringBuilder();
        int i = start;

        while (i < content.Length)
        {
            var c = content[i];
            switch (c)
            {
                case '(':
                    parenCount++;
                    sb.Append(c);
                    i++;
                    break;

                case ')':
                    parenCount--;
                    if (parenCount == 0)
                    {
                        // 找到匹配的 ')'，解析子表达式。
                        var exprText = sb.ToString();
                        var expr = ParseSubExpressionText(exprText, parentSpan);
                        var raw = "$(" + exprText + ")";
                        // consumed = exprText.Length + 2（'(' 和 ')'）—— 但调用方已消费 '$' 和 '('
                        // 实际：调用方 i 指向 '$'，i+1 是 '('，start = i+2 是 exprText 起点
                        // 返回 consumed = (i - start) + 1 = exprText.Length + 1（含 ')'）
                        return (expr, (i - start) + 1, raw);
                    }
                    sb.Append(c);
                    i++;
                    break;

                case '"':
                case '\'':
                    // 字符串字面量：扫描到匹配引号（含转义），原样追加。
                    var (str, strConsumed) = ScanStringLiteral(content, i, c);
                    sb.Append(str);
                    i += strConsumed;
                    break;

                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }

        // 未闭合的 $(expr)，返回 null（降级处理）。
        return (null, 0, string.Empty);
    }

    /// <summary>解析子表达式文本为 AST。借鉴 PS ParseNestedExpressions（Parser.cs:6367-6369）。</summary>
    /// <remarks>
    /// Per T-113：$(...) 内含语句（if/for/foreach 等）时返回 <see cref="StatementSubExpressionExpression"/>，
    /// 求值时执行语句块并返回末语句输出。借鉴 PS $(...) 语义——可含任意语句并返回管道输出。
    /// </remarks>
    private static Expression? ParseSubExpressionText(string exprText, SourceSpan span)
    {
        if (string.IsNullOrWhiteSpace(exprText))
            return null;

        try
        {
            // 用 ModernParser 解析子表达式（modern 语法）。
            var scriptBlock = ModernParser.Parse(exprText);
            if (scriptBlock.Statements.Count == 0)
                return null;

            // 单语句且为表达式语句：直接返回表达式。
            if (scriptBlock.Statements.Count == 1)
            {
                var stmt = scriptBlock.Statements[0];
                if (stmt is ExpressionStatement es)
                    return es.Expression;
                if (stmt is PipelineStatement ps && ps.Pipeline.Commands.Count == 1)
                    return ps.Pipeline.Commands[0];
            }

            // 含多语句或控制流语句（if/for/foreach 等）：返回 StatementSubExpressionExpression。
            // 借鉴 PS $(...) 语义：执行语句块，收集管道输出。Per T-113。
            return new StatementSubExpressionExpression(scriptBlock.Statements, span);
        }
        catch (ParserException)
        {
            // 子表达式解析失败，返回 null（降级为原样输出）。
            return null;
        }
    }

    /// <summary>扫描字符串字面量（含转义），返回完整字符串文本（含引号）+ 消费字符数。</summary>
    private static (string text, int consumed) ScanStringLiteral(string content, int start, char quote)
    {
        var sb = new StringBuilder();
        sb.Append(quote);
        int i = start + 1;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == quote)
            {
                // 检查双引号转义（"" → "）。
                if (i + 1 < content.Length && content[i + 1] == quote)
                {
                    sb.Append(c).Append(content[i + 1]);
                    i += 2;
                    continue;
                }
                sb.Append(c);
                return (sb.ToString(), i - start + 1);
            }
            if (c == '`' && i + 1 < content.Length)
            {
                // backtick 转义，原样保留两字符。
                sb.Append(c).Append(content[i + 1]);
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        // 未闭合字符串，返回已扫描部分。
        return (sb.ToString(), i - start);
    }

    /// <summary>判断字符是否可作为无括号变量名首字符（借鉴 PS IsVariableStart）。</summary>
    private static bool IsVariableNameFirstChar(char c)
        => c == '?' || c == '_' || char.IsLetter(c) || char.IsDigit(c);

    /// <summary>扫描变量名（含 $env:NAME / $global:x / $? 形式）。返回 (name, endIdx)。</summary>
    private static (string name, int end) ScanVariableName(string content, int start)
    {
        int end = start;
        while (end < content.Length && (content[end] == '?' || content[end] == '_'
            || char.IsLetterOrDigit(content[end]) || content[end] == ':'))
        {
            end++;
        }
        return (content[start..end], end);
    }

    /// <summary>扫描变量后的 .Property / [index] 后缀链。返回 (suffix, endIdx)。</summary>
    private static (string suffix, int end) ScanSuffix(string content, int start)
    {
        int end = start;
        var sb = new StringBuilder();
        while (end < content.Length)
        {
            if (content[end] == '.')
            {
                end++;
                var propStart = end;
                while (end < content.Length && (char.IsLetterOrDigit(content[end]) || content[end] == '_'))
                    end++;
                if (end == propStart) break;
                sb.Append(content[(propStart - 1)..end]);
            }
            else if (content[end] == '[')
            {
                int depth = 1;
                end++;
                var idxStart = end;
                while (end < content.Length && depth > 0)
                {
                    if (content[end] == '[') depth++;
                    else if (content[end] == ']') depth--;
                    if (depth > 0) end++;
                }
                if (end < content.Length) end++; // 跳过 ']'
                sb.Append("[").Append(content[idxStart..(end - 1)]).Append("]");
            }
            else
            {
                break;
            }
        }
        return (sb.ToString(), end);
    }

    /// <summary>根据变量名构造 VariableExpression（处理 scope 前缀）。</summary>
    private static Expression BuildVariableExpression(string name, SourceSpan span)
    {
        var (scope, baseName) = ParseScope(name);
        return new VariableExpression(baseName, scope, span);
    }

    /// <summary>
    /// 判断 ${...} 内的内容是否为简单变量名（含 scope: 前缀）。Per ADR-0050 §6.4 + T-083.
    /// 简单变量名：字母/下划线/数字/冒号（scope 前缀）组成，无运算符/空格/表达式字符。
    /// </summary>
    private static bool IsSimpleVariableName(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '?'))
                return false;
        }
        return true;
    }

    /// <summary>构造带后缀的变量表达式（$var.Prop / $arr[i]）。</summary>
    private static Expression BuildVariableWithSuffix(string name, string suffix, SourceSpan span)
    {
        var (scope, baseName) = ParseScope(name);
        var varExpr = new VariableExpression(baseName, scope, span);

        if (string.IsNullOrEmpty(suffix))
            return varExpr;

        // 解析后缀链：.Prop / [index]
        Expression current = varExpr;
        int i = 0;
        while (i < suffix.Length)
        {
            if (suffix[i] == '.')
            {
                i++;
                var propStart = i;
                while (i < suffix.Length && (char.IsLetterOrDigit(suffix[i]) || suffix[i] == '_'))
                    i++;
                var propName = suffix[propStart..i];
                if (string.IsNullOrEmpty(propName)) break;
                current = new MemberExpression(current, propName, Static: false, Arguments: null, NullConditional: false, span);
            }
            else if (suffix[i] == '[')
            {
                var close = suffix.IndexOf(']', i);
                if (close < 0) break;
                var indexText = suffix[(i + 1)..close];
                // 简化：索引表达式用字面量（数字或字符串）。
                Expression indexExpr;
                if (int.TryParse(indexText, out var intVal))
                    indexExpr = new LiteralExpression(intVal, LiteralKind.Integer, span);
                else
                    indexExpr = new LiteralExpression(indexText.Trim('"', '\''), LiteralKind.String, span);
                current = new IndexExpression(current, indexExpr, span);
                i = close + 1;
            }
            else
            {
                break;
            }
        }
        return current;
    }

    /// <summary>解析变量 scope 前缀（global:/script:/local:/private:/using:/env:）。</summary>
    private static (VariableScopeKind scope, string name) ParseScope(string name)
    {
        if (name.Contains(':'))
        {
            var idx = name.IndexOf(':');
            var prefix = name[..idx].ToLowerInvariant();
            var rest = name[(idx + 1)..];
            return prefix switch
            {
                "global" => (VariableScopeKind.Global, rest),
                "script" => (VariableScopeKind.Script, rest),
                "local" => (VariableScopeKind.Local, rest),
                "private" => (VariableScopeKind.Private, rest),
                "using" => (VariableScopeKind.Using, rest),
                "env" => (VariableScopeKind.Environment, rest),
                _ => (VariableScopeKind.Default, name),
            };
        }
        return (VariableScopeKind.Default, name);
    }
}
