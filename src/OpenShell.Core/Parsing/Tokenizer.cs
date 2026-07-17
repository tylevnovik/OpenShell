#nullable enable
// ADR-0045 §14 + ADR-0050 §1.2 共享 Tokenizer。
// 设计要点：
//   1. PowerShellParser 与 ModernParser 共享此 Tokenizer（per ADR-0050 §1.2）。
//   2. 双模式词法：PS 风格 (-eq -gt -and) 与 Modern 风格 (== > &&) 并存。
//   3. 上下文不敏感：tokenizer 输出原始 token，由 parser 决定命令模式 vs 表达式模式。
//   4. here-string 状态机：在 @"..."@ 内的特殊换行处理。
//   5. 数字字面量支持 0x/0b/KB/MB/GB/TB 单位（与 Filter.Lexer 保持一致）。

using System.Globalization;
using System.Text;

namespace OpenShell.Parsing;

/// <summary>
/// OpenShell 统一 Tokenizer。Per ADR-0045 §14 + ADR-0050 §1.2.
/// <para>
/// 词法支持：双引号字符串插值原样保留（由 evaluator 求值）、单引号原样、
/// here-string、$ 变量、[Type] 类型引用、@{} @() @"..."@、注释、关键字、
/// PS 运算符 (-eq -gt -and) 与 Modern 运算符 (== greater-than amp-amp)。
/// </para>
/// </summary>
public sealed class Tokenizer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _column = 1;
    private readonly List<Token> _tokens = new();

    /// <summary>构造 Tokenizer。</summary>
    public Tokenizer(string source)
    {
        _source = source ?? "";
        _pos = 0;
    }

    /// <summary> tokenize 整个源，返回 token 列表（含 End）。</summary>
    public IReadOnlyList<Token> Tokenize()
    {
        while (_pos < _source.Length)
        {
            var start = MakePosition();
            var ch = _source[_pos];

            // 1. 换行（统一为 \n）
            if (ch == '\r')
            {
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '\n') _pos++;
                _line++; _column = 1;
                AddToken(TokenKind.NewLine, "\\n", null, start);
                continue;
            }
            if (ch == '\n')
            {
                _pos++; _line++; _column = 1;
                AddToken(TokenKind.NewLine, "\\n", null, start);
                continue;
            }

            // 2. 空白（不输出 token，但消耗）
            if (ch == ' ' || ch == '\t')
            {
                _pos++; _column++;
                continue;
            }

            // 3. 行注释 #
            if (ch == '#')
            {
                LexLineComment(start);
                continue;
            }

            // 4. 块注释 <# #>
            if (ch == '<' && _pos + 1 < _source.Length && _source[_pos + 1] == '#')
            {
                LexBlockComment(start);
                continue;
            }

            // 5. 分号
            if (ch == ';') { Advance(); AddToken(TokenKind.Semicolon, ";", null, start); continue; }

            // 6. @ 开头的构造（@{ @ ( @" @' @ variable）
            if (ch == '@')
            {
                LexAt(start);
                continue;
            }

            // 7. $ 变量
            if (ch == '$')
            {
                LexVariable(start);
                continue;
            }

            // 8. [ 类型引用 ] 或 [ 索引 ]
            if (ch == '[')
            {
                if (TryLexTypeRef(start)) continue;
                Advance(); AddToken(TokenKind.LBracket, "[", null, start);
                continue;
            }
            if (ch == ']') { Advance(); AddToken(TokenKind.RBracket, "]", null, start); continue; }

            // 9. 括号
            if (ch == '{') { Advance(); AddToken(TokenKind.LBrace, "{", null, start); continue; }
            if (ch == '}') { Advance(); AddToken(TokenKind.RBrace, "}", null, start); continue; }
            if (ch == '(') { Advance(); AddToken(TokenKind.LParen, "(", null, start); continue; }
            if (ch == ')') { Advance(); AddToken(TokenKind.RParen, ")", null, start); continue; }

            // 10. 字符串（含三引号 """...""" 多行字符串，Per ADR-0050 §6.1/§6.2）
            if (ch == '"')
            {
                if (_pos + 2 < _source.Length && _source[_pos + 1] == '"' && _source[_pos + 2] == '"')
                {
                    LexTripleQuotedString(start);
                    continue;
                }
                LexString('"', start);
                continue;
            }
            if (ch == '\'')
            {
                LexString('\'', start);
                continue;
            }

            // 11. 数字
            if (char.IsDigit(ch))
            {
                LexNumber(start);
                continue;
            }

            // 12. - 开头（可能是负号、PS 运算符、命名参数）
            if (ch == '-')
            {
                if (TryLexDashOperatorOrParameter(start)) continue;
                // 单独的 -
                Advance(); AddToken(TokenKind.Minus, "-", null, start);
                continue;
            }

            // 13. 标识符 / 关键字 / 命令名
            //     特殊：r"..." 原始字符串（Per ADR-0050 §6.1/§6.3）。
            if (ch == 'r' && _pos + 1 < _source.Length && _source[_pos + 1] == '"')
            {
                LexRawString(start);
                continue;
            }
            if (char.IsLetter(ch) || ch == '_' || ch == '\\')
            {
                LexIdentifier(start);
                continue;
            }

            // 14. 多字符运算符
            if (TryLexMultiCharOperator(start)) continue;

            // 15. 单字符运算符
            LexSingleCharOperator(start);
        }

        AddToken(TokenKind.End, "", null, MakePosition());
        return _tokens;
    }

    // =========================================================================
    // 词法辅助方法
    // =========================================================================

    private SourcePosition MakePosition() => new(_line, _column, _pos);

    private void Advance()
    {
        _pos++; _column++;
    }

    private void AddToken(TokenKind kind, string text, object? value, SourcePosition start)
    {
        var end = MakePosition();
        _tokens.Add(new Token(kind, text, value, new SourceSpan(start, end)));
    }

    private char Peek(int offset = 0) =>
        _pos + offset < _source.Length ? _source[_pos + offset] : '\0';

    private bool Match(string text)
    {
        if (_pos + text.Length > _source.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (_source[_pos + i] != text[i]) return false;
        // 推进
        for (int i = 0; i < text.Length; i++) Advance();
        return true;
    }

    // ---------------------------------------------------------------------------
    // 注释
    // ---------------------------------------------------------------------------

    private void LexLineComment(SourcePosition start)
    {
        var sb = new StringBuilder();
        while (_pos < _source.Length && _source[_pos] != '\r' && _source[_pos] != '\n')
        {
            sb.Append(_source[_pos]);
            Advance();
        }
        var text = sb.ToString();
        // ADR-0050 §1.3: #lang ps1/osh { ... } 块切换指令。
        // 整行作为 LangDirective token 产出（保留完整文本供 parser 解析）。
        if (text.StartsWith("#lang ", StringComparison.OrdinalIgnoreCase))
        {
            // 多行 #lang 块：如果首行含 {，继续读到匹配的 }（跨行）。
            var fullText = ReadLangBlockIfMultiLine(text, start);
            AddToken(TokenKind.LangDirective, fullText, fullText, start);
            return;
        }
        AddToken(TokenKind.LineComment, text, null, start);
    }

    /// <summary>
    /// ADR-0050 §1.3: 如果 #lang 指令行包含 {，继续读取直到匹配的 }。
    /// 处理嵌套花括号、字符串、行注释。返回完整的 #lang 块文本。
    /// 若首行无 { 或花括号已闭合，返回原始首行文本。
    /// </summary>
    private string ReadLangBlockIfMultiLine(string firstLine, SourcePosition start)
    {
        var braceIdx = firstLine.IndexOf('{');
        if (braceIdx < 0) return firstLine;

        // 计算首行花括号深度
        int depth = 0;
        for (int i = braceIdx; i < firstLine.Length; i++)
        {
            var c = firstLine[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        if (depth <= 0) return firstLine; // 首行已闭合

        // 跨行读取直到 depth == 0
        var sb = new StringBuilder(firstLine);
        bool inString = false;
        char stringQuote = '\0';
        bool inLineComment = false;

        while (_pos < _source.Length && depth > 0)
        {
            var c = _source[_pos];

            if (inLineComment)
            {
                sb.Append(c);
                Advance();
                if (c == '\n') inLineComment = false;
                continue;
            }

            if (inString)
            {
                sb.Append(c);
                Advance();
                if (c == stringQuote)
                {
                    // PS 双引号转义：重复引号表示字面引号
                    if (_pos < _source.Length && _source[_pos] == stringQuote)
                    {
                        sb.Append(_source[_pos]);
                        Advance();
                    }
                    else
                    {
                        inString = false;
                    }
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                stringQuote = c;
                sb.Append(c);
                Advance();
                continue;
            }

            if (c == '#')
            {
                // 行注释（非 #lang，因为已在块内）
                inLineComment = true;
                sb.Append(c);
                Advance();
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}') depth--;

            sb.Append(c);
            Advance();
        }

        return sb.ToString();
    }

    private void LexBlockComment(SourcePosition start)
    {
        // 已知开头是 <#
        Advance(); Advance(); // 消费 <#
        var sb = new StringBuilder("<#");
        while (_pos < _source.Length)
        {
            if (_source[_pos] == '#' && Peek(1) == '>')
            {
                sb.Append("#>");
                Advance(); Advance();
                break;
            }
            var c = _source[_pos];
            sb.Append(c);
            if (c == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }
        AddToken(TokenKind.BlockComment, sb.ToString(), null, start);
    }

    // ---------------------------------------------------------------------------
    // @ 构造
    // ---------------------------------------------------------------------------

    private void LexAt(SourcePosition start)
    {
        // @" here-string double
        if (Peek(1) == '"' && Peek(2) == '\r' && Peek(3) == '\n')
        {
            LexHereString(start, singleQuote: false);
            return;
        }
        if (Peek(1) == '"' && Peek(2) == '\n')
        {
            LexHereString(start, singleQuote: false);
            return;
        }
        if (Peek(1) == '\'' && (Peek(2) == '\n' || (Peek(2) == '\r' && Peek(3) == '\n')))
        {
            LexHereString(start, singleQuote: true);
            return;
        }
        // @{ hash literal — emit '@' alone; the main loop emits '{' separately.
        if (Peek(1) == '{') { Advance(); AddToken(TokenKind.At, "@", null, start); return; }
        // @( array literal — emit '@' alone; the main loop emits '(' separately.
        if (Peek(1) == '(') { Advance(); AddToken(TokenKind.At, "@", null, start); return; }
        // @$ variable splat
        if (Peek(1) == '$' || char.IsLetterOrDigit(Peek(1)))
        {
            Advance(); AddToken(TokenKind.At, "@", null, start);
            return;
        }
        Advance(); AddToken(TokenKind.At, "@", null, start);
    }

    private void LexHereString(SourcePosition start, bool singleQuote)
    {
        var quote = singleQuote ? '\'' : '"';
        // 消费 @" 或 @'
        Advance(); Advance();
        // 消费换行（首换行是 here-string 语法分隔符，不进入 body）
        if (_source[_pos] == '\r') { _pos++; _line++; _column = 1; }
        if (_source[_pos] == '\n') { _pos++; _line++; _column = 1; }

        var sb = new StringBuilder();
        while (_pos < _source.Length)
        {
            // 检查闭合 "@ 或 '@，必须在行首
            if (_column == 1 && _source[_pos] == quote && Peek(1) == '@')
            {
                Advance(); Advance();
                AddToken(singleQuote ? TokenKind.HereSingleString : TokenKind.HereString,
                    sb.ToString(), sb.ToString(), start);
                return;
            }
            var c = _source[_pos];

            // 双引号 here-string 处理 ` 转义（PS 风格，与 LexString 一致）。
            // 单引号 here-string 不处理任何转义。Per T-107（借鉴 PS tokenizer.cs:2755-2865）。
            if (!singleQuote && c == '`')
            {
                Advance(); // 消费 `
                if (_pos >= _source.Length) break;
                var esc = _source[_pos];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    '"' => '"',
                    '`' => '`',
                    '$' => '$',
                    '\\' => '\\',
                    _ => esc,  // 不识别的转义保留原字符
                });
                Advance(); // 消费转义字符
                continue;
            }

            sb.Append(c);
            if (c == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }
        AddToken(singleQuote ? TokenKind.HereSingleString : TokenKind.HereString,
            sb.ToString(), sb.ToString(), start);
    }

    // ---------------------------------------------------------------------------
    // 字符串
    // ---------------------------------------------------------------------------

    private void LexString(char quote, SourcePosition start)
    {
        Advance(); // 消费开头引号
        var sb = new StringBuilder();
        var isSingle = quote == '\'';
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == quote)
            {
                // 双引号字符串中两个 "" 表示一个 "
                if (!isSingle && Peek(1) == '"')
                {
                    sb.Append('"');
                    Advance(); Advance();
                    continue;
                }
                // 单引号字符串中两个 '' 表示一个 '
                if (isSingle && Peek(1) == '\'')
                {
                    sb.Append('\'');
                    Advance(); Advance();
                    continue;
                }
                Advance(); // 消费结束引号
                AddToken(isSingle ? TokenKind.SingleString : TokenKind.String,
                    sb.ToString(), sb.ToString(), start);
                return;
            }
            // 双引号字符串中的转义（PS 风格：` 转义符）
            if (!isSingle && c == '`')
            {
                Advance();
                if (_pos >= _source.Length) break;
                var esc = _source[_pos];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    '"' => '"',
                    '`' => '`',
                    '$' => '$',
                    '\\' => '\\',
                    _ => esc,  // 不识别的转义保留原字符
                });
                Advance();
                continue;
            }
            sb.Append(c);
            if (c == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }
        AddToken(isSingle ? TokenKind.SingleString : TokenKind.String, sb.ToString(), sb.ToString(), start);
    }

    /// <summary>
    /// 词法分析原始字符串 r"..."（Per ADR-0050 §6.1/§6.3）。
    /// 不处理转义（反斜杠原样保留）、不插值（$ 原样保留）。
    /// 借鉴 Rust r"..." / Python r"..."。
    /// </summary>
    private void LexRawString(SourcePosition start)
    {
        Advance(); // 消费 r
        Advance(); // 消费开头 "
        var sb = new StringBuilder();
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == '"')
            {
                Advance(); // 消费结束引号
                AddToken(TokenKind.RawString, sb.ToString(), sb.ToString(), start);
                return;
            }
            sb.Append(c);
            if (c == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }
        // 未闭合的原始字符串：返回已收集内容（与 LexString 行为一致）。
        AddToken(TokenKind.RawString, sb.ToString(), sb.ToString(), start);
    }

    /// <summary>
    /// 词法分析三引号多行字符串 """..."""（Per ADR-0050 §6.1/§6.2）。
    /// 闭合 """ 无需在行首。当前实现不处理 $var 插值（简化版，后续可扩展）。
    /// 替代 PowerShell here-string @"..."@。
    /// Per ADR-0050 §6.2: 缩进处理 - 闭合 """ 的缩进决定公共前缀剥离 (与 Kotlin 一致)。
    /// </summary>
    private void LexTripleQuotedString(SourcePosition start)
    {
        Advance(); Advance(); Advance(); // 消费开头 """
        var sb = new StringBuilder();
        int closingIndent = -1; // 闭合 """ 所在行的前导空白长度, -1 表示未确定
        while (_pos < _source.Length)
        {
            // 检测闭合 """
            if (_source[_pos] == '"' &&
                _pos + 2 < _source.Length &&
                _source[_pos + 1] == '"' &&
                _source[_pos + 2] == '"')
            {
                // 计算闭合 """ 所在行的前导空白长度。
                closingIndent = ComputeLineLeadingWhitespace(sb.ToString(), sb.Length);
                Advance(); Advance(); Advance(); // 消费结束 """
                break;
            }
            var c = _source[_pos];
            sb.Append(c);
            if (c == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }

        var content = sb.ToString();
        // Per ADR-0050 §6.2: 缩进处理 - 剥离公共前缀。
        // 闭合 """ 的缩进决定剥离量: 每行去掉 min(closingIndent, 该行前导空白) 个前导空白字符。
        if (closingIndent > 0)
        {
            content = StripCommonIndent(content, closingIndent);
        }
        // Per ADR-0050 §6.2: 内部换行保留为 \n (跨平台统一为 LF)。
        // 如果首字符是换行 (""" 后直接换行), 去掉首换行 (Kotlin 行为)。
        if (content.Length > 0 && content[0] == '\n')
            content = content[1..];
        else if (content.Length >= 2 && content[0] == '\r' && content[1] == '\n')
            content = content[2..];
        // 去除闭合 """ 所在行的空行（缩进剥离后为空的尾部行）。
        if (content.Length > 0 && content[^1] == '\n')
            content = content[..^1];

        AddToken(TokenKind.String, content, content, start);
    }

    /// <summary>计算闭合 """ 所在行的前导空白长度。</summary>
    private static int ComputeLineLeadingWhitespace(string content, int length)
    {
        // 从 content 末尾向前找当前行的开头 (上一个 \n 之后或字符串开头)。
        int lineStart = length;
        for (int i = length - 1; i >= 0; i--)
        {
            if (content[i] == '\n') { lineStart = i + 1; break; }
            if (content[i] != ' ' && content[i] != '\t') { lineStart = i + 1; break; }
            lineStart = i;
        }
        // 计算从 lineStart 到闭合 """ 之间的空白长度。
        int indent = 0;
        for (int i = lineStart; i < length; i++)
        {
            if (content[i] == ' ' || content[i] == '\t') indent++;
            else break;
        }
        return indent;
    }

    /// <summary>剥离每行的公共前缀缩进 (最多 maxIndent 个字符)。Per ADR-0050 §6.2.</summary>
    private static string StripCommonIndent(string content, int maxIndent)
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int strip = 0;
            while (strip < maxIndent && strip < lines[i].Length &&
                   (lines[i][strip] == ' ' || lines[i][strip] == '\t'))
                strip++;
            if (strip > 0)
                lines[i] = lines[i][strip..];
        }
        return string.Join('\n', lines);
    }

    // ---------------------------------------------------------------------------
    // 数字
    // ---------------------------------------------------------------------------

    private void LexNumber(SourcePosition start)
    {
        var sb = new StringBuilder();
        bool isDouble = false;
        // isHexOrBinary: 0x/0b 分支已直接算出 long 值，无需再 long.Parse(numStr)。
        bool isHexOrBinary = false;
        long intValue = 0;

        // 0x 十六进制
        if (_source[_pos] == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            sb.Append(_source[_pos]); sb.Append(_source[_pos + 1]);
            Advance(); Advance();
            while (_pos < _source.Length && IsHexDigit(_source[_pos]))
            {
                sb.Append(_source[_pos]); Advance();
            }
            intValue = long.Parse(sb.ToString(2, sb.Length - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            isHexOrBinary = true;
        }
        // 0b 二进制
        else if (_source[_pos] == '0' && (Peek(1) == 'b' || Peek(1) == 'B'))
        {
            sb.Append(_source[_pos]); sb.Append(_source[_pos + 1]);
            Advance(); Advance();
            while (_pos < _source.Length && (_source[_pos] == '0' || _source[_pos] == '1'))
            {
                sb.Append(_source[_pos]); Advance();
            }
            var binStr = sb.ToString(2, sb.Length - 2);
            intValue = Convert.ToInt64(binStr, 2);
            isHexOrBinary = true;
        }
        else
        {
            // 普通数字
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
            {
                sb.Append(_source[_pos]); Advance();
            }
            if (_pos < _source.Length && _source[_pos] == '.' && Peek(1) != '.')
            {
                isDouble = true;
                sb.Append('.'); Advance();
                while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                {
                    sb.Append(_source[_pos]); Advance();
                }
            }
            // 指数 e+10
            if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
            {
                isDouble = true;
                sb.Append(_source[_pos]); Advance();
                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                {
                    sb.Append(_source[_pos]); Advance();
                }
                while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                {
                    sb.Append(_source[_pos]); Advance();
                }
            }
        }

        // 类型后缀 (PowerShell: d=decimal→double, l=long, u=uint, y=sbyte, s=short,
        //   组合: ul/lu=ulong, uy=byte, us/su=ushort). Per T-110（借鉴 PS tokenizer.cs:4025-4083）。
        // 注: 'f' (float) 和 'm' (decimal/money) 是 C# 后缀, 不是 PowerShell 后缀;
        //   不支持以避免与 KB/MB/GB 单位前缀冲突 (Per ADR-0012 §5 字面量).
        //   后缀字符不追加到 sb, 保证 numStr 可被 long/double.Parse 正确解析.
        //   后缀类型信息记录在 numberTypeHint, 据此将 token.Value 转换为对应 .NET 类型
        //   (byte/sbyte/short/ushort/uint/ulong), 让 evaluator 直接获得正确类型对象。
        string? numberTypeHint = null;
        if (_pos < _source.Length)
        {
            var suffix = char.ToLowerInvariant(_source[_pos]);
            if (suffix is 'd' or 'l' or 'u' or 'y' or 's')
            {
                if (suffix == 'd') isDouble = true;
                // 检查组合后缀（ul/lu/uy/us/su）。
                if (_pos + 1 < _source.Length)
                {
                    var suffix2 = char.ToLowerInvariant(_source[_pos + 1]);
                    if (suffix == 'u' && suffix2 == 'l') { numberTypeHint = "ulong"; Advance(); Advance(); }
                    else if (suffix == 'l' && suffix2 == 'u') { numberTypeHint = "ulong"; Advance(); Advance(); }
                    else if (suffix == 'u' && suffix2 == 'y') { numberTypeHint = "byte"; Advance(); Advance(); }
                    else if (suffix == 'u' && suffix2 == 's') { numberTypeHint = "ushort"; Advance(); Advance(); }
                    else if (suffix == 's' && suffix2 == 'u') { numberTypeHint = "ushort"; Advance(); Advance(); }
                    else
                    {
                        // 单字符后缀。
                        numberTypeHint = suffix switch
                        {
                            'u' => "uint",
                            'l' => "long",
                            'y' => "sbyte",
                            's' => "short",
                            _ => null,
                        };
                        Advance();
                    }
                }
                else
                {
                    numberTypeHint = suffix switch
                    {
                        'u' => "uint",
                        'l' => "long",
                        'y' => "sbyte",
                        's' => "short",
                        _ => null,
                    };
                    Advance();
                }
            }
        }

        // 数量单位 KB/MB/GB/TB/PB (1024 进制, PowerShell 语义; Per ADR-0012 §5 字面量).
        long multiplier = 1;
        if (_pos + 1 < _source.Length)
        {
            var u1 = char.ToUpperInvariant(_source[_pos]);
            var u2 = char.ToUpperInvariant(Peek(1));
            if (u1 == 'K' && u2 == 'B') { multiplier = 1024L; Advance(); Advance(); }
            else if (u1 == 'M' && u2 == 'B') { multiplier = 1024L * 1024; Advance(); Advance(); }
            else if (u1 == 'G' && u2 == 'B') { multiplier = 1024L * 1024 * 1024; Advance(); Advance(); }
            else if (u1 == 'T' && u2 == 'B') { multiplier = 1024L * 1024 * 1024 * 1024; Advance(); Advance(); }
            else if (u1 == 'P' && u2 == 'B') { multiplier = 1024L * 1024 * 1024 * 1024 * 1024; Advance(); Advance(); }
        }

        var numStr = sb.ToString();
        if (isDouble)
        {
            var value = double.Parse(numStr, CultureInfo.InvariantCulture) * multiplier;
            AddToken(TokenKind.Double, numStr, value, start);
        }
        else
        {
            // 普通整数: 解析 numStr; 十六进制/二进制: 已直接算出 intValue。
            if (!isHexOrBinary)
                intValue = long.Parse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var longValue = intValue * multiplier;
            // 根据后缀类型把 value 转换为目标 .NET 类型。Per T-110。
            // 默认 (无后缀) 为 long; 有后缀则装箱为对应整数类型, evaluator/类型检查可直接使用。
            object typedValue = numberTypeHint switch
            {
                "byte"    => (byte)longValue,
                "sbyte"   => (sbyte)longValue,
                "short"   => (short)longValue,
                "ushort"  => (ushort)longValue,
                "uint"    => (uint)longValue,
                "ulong"   => (ulong)longValue,
                "long"    => longValue,
                _         => longValue,  // 默认 long
            };
            AddToken(TokenKind.Integer, numStr, typedValue, start);
        }
    }

    private static bool IsHexDigit(char c) =>
        char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // ---------------------------------------------------------------------------
    // 变量
    // ---------------------------------------------------------------------------

    private void LexVariable(SourcePosition start)
    {
        Advance(); // 消费 $

        // ${name} 形式
        if (_pos < _source.Length && _source[_pos] == '{')
        {
            Advance(); // 消费 {
            var sb = new StringBuilder();
            while (_pos < _source.Length && _source[_pos] != '}')
            {
                sb.Append(_source[_pos]); Advance();
            }
            if (_pos < _source.Length) Advance(); // 消费 }
            AddToken(TokenKind.Variable, "${" + sb + "}", sb.ToString(), start);
            return;
        }

        // $env:NAME / $global:x / $script:x / $local:x / $private:x / $using:x
        if (_pos < _source.Length && char.IsLetter(_source[_pos]))
        {
            var sb = new StringBuilder();
            while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            {
                sb.Append(_source[_pos]); Advance();
            }
            var scopeName = sb.ToString();
            if (_pos < _source.Length && _source[_pos] == ':' &&
                (scopeName.Equals("env", StringComparison.OrdinalIgnoreCase) ||
                 scopeName.Equals("global", StringComparison.OrdinalIgnoreCase) ||
                 scopeName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                 scopeName.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                 scopeName.Equals("private", StringComparison.OrdinalIgnoreCase) ||
                 scopeName.Equals("using", StringComparison.OrdinalIgnoreCase)))
            {
                Advance(); // 消费 :
                var nameSb = new StringBuilder();
                while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                {
                    nameSb.Append(_source[_pos]); Advance();
                }
                var fullName = scopeName + ":" + nameSb;
                var kind = scopeName.ToLowerInvariant() switch
                {
                    "env" => TokenKind.EnvVariable,
                    "global" => TokenKind.ScopedVariable,
                    "script" => TokenKind.ScopedVariable,
                    "local" => TokenKind.ScopedVariable,
                    "private" => TokenKind.ScopedVariable,
                    "using" => TokenKind.ScopedVariable,
                    _ => TokenKind.Variable,
                };
                AddToken(kind, "$" + fullName, fullName, start);
                return;
            }
            // 普通变量
            AddToken(TokenKind.Variable, "$" + scopeName, scopeName, start);
            return;
        }

        // $_ $? $! 等特殊变量
        if (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == '_' || c == '?' || c == '$' || c == '^')
            {
                Advance();
                AddToken(TokenKind.Variable, "$" + c, c.ToString(), start);
                return;
            }
            // $.Name 简写：$. 等价 $_.（Per ADR-0050 §4.1/§4.2）。
            // 当 '.' 后跟标识符字符时，Token 层仅发出名为 "_" 的 Variable token，
            // "." 由后续主循环处理为 Dot 运算符（如 $.Name → $_.Name）。
            // 当 '.' 后不跟标识符字符时，$. 单独使用也等价 $_（Per ADR-0050 §4.1 表格），
            // 此时消费 '.' 并发出 Variable("_")。
            if (c == '.')
            {
                var nextChar = (_pos + 1 < _source.Length) ? _source[_pos + 1] : '\0';
                if (char.IsLetterOrDigit(nextChar) || nextChar == '_')
                {
                    // $.Name 形式：不消费 '.'，留给主循环作为 Dot token
                    AddToken(TokenKind.Variable, "$_", "_", start);
                    return;
                }
                // $. 单独使用形式：消费 '.'，等价 $_
                Advance(); // 消费 .
                AddToken(TokenKind.Variable, "$_", "_", start);
                return;
            }
        }

        // ADR-0050 §4.1: $ 单独使用 = $_ 当前管道对象。
        AddToken(TokenKind.Variable, "$_", "_", start);
    }

    // ---------------------------------------------------------------------------
    // 类型引用 [System.IO.File]
    // ---------------------------------------------------------------------------

    private bool TryLexTypeRef(SourcePosition start)
    {
        // 借鉴 PS ScanTypeName（tokenizer.cs:4462-4493）。
        // 识别 [TypeName] / [TypeName[]] / [TypeName[,...]] / [Generic[Args]] / [Namespace.Type] 等。
        // 回退策略：若内容不像类型引用（如 [0] 索引、[CmdletBinding(...)] 特性），回退让 parser 当索引/特性处理。
        var savePos = _pos;
        var saveLine = _line;
        var saveCol = _column;
        Advance(); // 消费 [
        var sb = new StringBuilder();
        var depth = 1;
        while (_pos < _source.Length && depth > 0)
        {
            var c = _source[_pos];
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) break;
            }
            sb.Append(c);
            if (c == '\n') { _line++; _column = 1; }
            else _column++;
            _pos++;
        }
        if (_pos < _source.Length && _source[_pos] == ']')
        {
            Advance(); // 消费 ]
            var text = sb.ToString();
            // 类型引用判定（放宽以支持小写别名 int/string/bool + 泛型 [List[int]] + 多参数 [Dict[string,int]]）：
            //   1. 非空，首字符是字母/下划线/点（排除 [0] 数字索引、["str"] 字符串索引）
            //   2. 不含 '(' （[CmdletBinding(...)] 是特性，由 parser 处理）
            //   3. 不含空白
            if (text.Length > 0
                && (char.IsLetter(text[0]) || text[0] == '_' || text[0] == '.')
                && !text.Contains('(')
                && !text.Any(char.IsWhiteSpace))
            {
                AddToken(TokenKind.TypeRef, "[" + text + "]", text, start);
                return true;
            }
        }
        // 回退，让 parser 当作索引处理
        _pos = savePos; _line = saveLine; _column = saveCol;
        return false;
    }

    // ---------------------------------------------------------------------------
    // - 开头：负号 / PS 运算符 / 命名参数
    // ---------------------------------------------------------------------------

    private bool TryLexDashOperatorOrParameter(SourcePosition start)
    {
        // 负号或数字：-123 是数字
        if (_pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            return false;  // 让数字处理
        }

        // PS 运算符：-eq -ne -gt -lt -le -ge -and -or -not -like -match -in -contains -is -as -band -bor -shl -shr ...
        if (_pos + 1 < _source.Length && char.IsLetter(_source[_pos + 1]))
        {
            // 收集 -word
            var savePos = _pos;
            Advance(); // 消费 -
            var sb = new StringBuilder();
            while (_pos < _source.Length && char.IsLetter(_source[_pos]))
            {
                sb.Append(_source[_pos]); Advance();
            }
            var word = sb.ToString();
            var lowerWord = word.ToLowerInvariant();

            // 检查是否是已知 PS 运算符
            if (TryMapPsOperator(lowerWord, out var opKind))
            {
                AddToken(opKind, "-" + word, null, start);
                return true;
            }

            // 不是运算符：作为命名参数 -Name 或 switch -Recurse
            // 检查后续是否是 :value 形式
            if (_pos < _source.Length && _source[_pos] == ':')
            {
                Advance(); // 消费 :
                AddToken(TokenKind.NamedParameter, "-" + word + ":", word, start);
            }
            else
            {
                AddToken(TokenKind.SwitchParameter, "-" + word, word, start);
            }
            return true;
        }

        // -> fn 返回类型注解（modern，ADR-0050 §3.2）；=> lambda 由 TryLexMultiCharOperator 处理
        // -= -- 复合赋值/递减
        if (_pos + 1 < _source.Length)
        {
            var next = _source[_pos + 1];
            if (next == '>') { Advance(); Advance(); AddToken(TokenKind.RightArrow, "->", null, start); return true; }
            if (next == '=') { Advance(); Advance(); AddToken(TokenKind.MinusAssign, "-=", null, start); return true; }
            if (next == '-') { Advance(); Advance(); AddToken(TokenKind.MinusMinus, "--", null, start); return true; }
        }

        return false;  // 单独的 -
    }

    private static bool TryMapPsOperator(string word, out TokenKind kind)
    {
        kind = word switch
        {
            "eq" => TokenKind.CmpEq,
            "ne" => TokenKind.CmpNe,
            "gt" => TokenKind.CmpGt,
            "lt" => TokenKind.CmpLt,
            "ge" => TokenKind.CmpGe,
            "le" => TokenKind.CmpLe,
            "like" => TokenKind.CmpLike,
            "notlike" => TokenKind.CmpNotLike,
            "match" => TokenKind.CmpMatch,
            "notmatch" => TokenKind.CmpNotMatch,
            "in" => TokenKind.CmpIn,
            "notin" => TokenKind.CmpNotIn,
            "contains" => TokenKind.CmpContains,
            "notcontains" => TokenKind.CmpNotContains,
            "is" => TokenKind.CmpIs,
            "isnot" => TokenKind.CmpIsNot,
            "as" => TokenKind.CmpAs,
            "and" => TokenKind.LogicalAnd,
            "or" => TokenKind.LogicalOr,
            "not" => TokenKind.LogicalNot,
            "xor" => TokenKind.LogicalXor,
            "band" => TokenKind.CmpBand,
            "bor" => TokenKind.CmpBor,
            "bxor" => TokenKind.BcmpBxor,
            "shl" => TokenKind.CmpShl,
            "shr" => TokenKind.CmpShr,
            _ => TokenKind.End,
        };
        return kind != TokenKind.End;
    }

    // ---------------------------------------------------------------------------
    // 标识符 / 关键字 / 命令名
    // ---------------------------------------------------------------------------

    private void LexIdentifier(SourcePosition start)
    {
        var sb = new StringBuilder();
        // 命令名允许 \ / : 等特殊字符（用于路径调用 & "C:\path\to\script.ps1"）
        // 简化：标识符可以含字母数字下划线 + - + . :
        // 但 - 开头已处理，这里只处理 [a-zA-Z_] 开头
        while (_pos < _source.Length && IsIdentChar(_source[_pos]))
        {
            sb.Append(_source[_pos]); Advance();
        }
        // 命令名可能含 -（verb-noun）：Get-ChildItem
        // 但需注意 -eq 等已 lex 为运算符。这里再检查是否是命令名形式：
        // 标识符后跟 - 再跟字母 = verb-noun
        while (_pos < _source.Length && _source[_pos] == '-' &&
               _pos + 1 < _source.Length && char.IsLetter(_source[_pos + 1]))
        {
            // 看是否是运算符 -eq 等
            var savePos = _pos;
            var saveCol = _column;
            Advance(); // -
            var opSb = new StringBuilder();
            while (_pos < _source.Length && char.IsLetter(_source[_pos]))
            {
                opSb.Append(_source[_pos]); Advance();
            }
            var opWord = opSb.ToString().ToLowerInvariant();
            if (TryMapPsOperator(opWord, out _))
            {
                // 是运算符，回退让后续处理
                _pos = savePos; _column = saveCol;
                break;
            }
            // 是 verb-noun 的一部分
            sb.Append('-');
            sb.Append(opSb);
        }

        var text = sb.ToString();
        var lower = text.ToLowerInvariant();
        if (IsKeyword(lower, out var kwKind))
        {
            AddToken(kwKind, text, null, start);
        }
        else if (lower == "true" || lower == "false")
        {
            AddToken(TokenKind.Boolean, text, lower == "true", start);
        }
        else if (lower == "null")
        {
            AddToken(TokenKind.Null, text, null, start);
        }
        else
        {
            AddToken(TokenKind.Identifier, text, null, start);
        }
    }

    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '\\';

    /// <summary>相对路径片段字符（.. / ../foo / ../../bar 等）。</summary>
    private static bool IsRelativePathChar(char c) =>
        char.IsLetterOrDigit(c) || c == '/' || c == '\\' || c == '.' || c == '_' || c == '-';

    private static bool IsKeyword(string word, out TokenKind kind)
    {
        kind = word switch
        {
            "if" or "elseif" or "elif" or "else" => TokenKind.Keyword,
            "switch" => TokenKind.Keyword,
            "while" => TokenKind.Keyword,
            "do" => TokenKind.Keyword,
            "until" => TokenKind.Keyword,
            "for" => TokenKind.Keyword,
            "foreach" => TokenKind.Keyword,
            "try" => TokenKind.Keyword,
            "catch" => TokenKind.Keyword,
            "finally" => TokenKind.Keyword,
            "function" => TokenKind.Keyword,
            "filter" => TokenKind.Keyword,
            "fn" => TokenKind.Keyword,  // modern fn 简写（Per ADR-0050 §3.1）
            "return" => TokenKind.Keyword,
            "break" => TokenKind.Keyword,
            "continue" => TokenKind.Keyword,
            "throw" => TokenKind.Keyword,
            "exit" => TokenKind.Keyword,
            "param" => TokenKind.Keyword,
            "using" => TokenKind.Keyword,
            "in" => TokenKind.Keyword,
            "trap" => TokenKind.Keyword,
            "match" => TokenKind.Keyword,  // modern match 表达式
            // ADR-0051: async / await 用户级异步关键字（modern 语法独有）。
            "async" => TokenKind.Keyword,
            "await" => TokenKind.Keyword,
            // ADR-0056: export/import/from/as 用于 ESM 风格模块导出导入（modern 语法独有）。
            "export" => TokenKind.Keyword,
            "import" => TokenKind.Keyword,
            "from" => TokenKind.Keyword,
            "as" => TokenKind.Keyword,
            // ADR-0012: `where` / `select` 是 Where-Object / Select-Object 的命令别名，
            // 不是保留关键字。让它们成为普通标识符，可在管道上下文中作为命令名使用。
            "default" => TokenKind.Keyword,
            "end" => TokenKind.Keyword,
            "begin" => TokenKind.Keyword,
            "process" => TokenKind.Keyword,
            "clean" => TokenKind.Keyword,
            _ => TokenKind.End,
        };
        return kind != TokenKind.End;
    }

    // ---------------------------------------------------------------------------
    // 多字符运算符
    // ---------------------------------------------------------------------------

    private bool TryLexMultiCharOperator(SourcePosition start)
    {
        var ch = _source[_pos];
        var next = Peek(1);

        switch (ch)
        {
            case '=':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.Equals, "==", null, start); return true; }
                if (next == '>') { Advance(); Advance(); AddToken(TokenKind.Arrow, "=>", null, start); return true; }
                Advance(); AddToken(TokenKind.Assign, "=", null, start); return true;

            case '!':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.NotEquals, "!=", null, start); return true; }
                Advance(); AddToken(TokenKind.Bang, "!", null, start); return true;

            case '<':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.Le, "<=", null, start); return true; }
                if (next == '<') { Advance(); Advance(); AddToken(TokenKind.Shl, "<<", null, start); return true; }
                Advance(); AddToken(TokenKind.Lt, "<", null, start); return true;

            case '>':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.Ge, ">=", null, start); return true; }
                if (next == '>') { Advance(); Advance(); AddToken(TokenKind.Shr, ">>", null, start); return true; }
                Advance(); AddToken(TokenKind.Gt, ">", null, start); return true;

            case '+':
                if (next == '+') { Advance(); Advance(); AddToken(TokenKind.PlusPlus, "++", null, start); return true; }
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.PlusAssign, "+=", null, start); return true; }
                Advance(); AddToken(TokenKind.Plus, "+", null, start); return true;

            case '-':
                if (next == '-') { Advance(); Advance(); AddToken(TokenKind.MinusMinus, "--", null, start); return true; }
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.MinusAssign, "-=", null, start); return true; }
                Advance(); AddToken(TokenKind.Minus, "-", null, start); return true;

            case '*':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.StarAssign, "*=", null, start); return true; }
                if (next == '*') { Advance(); Advance(); AddToken(TokenKind.Caret, "**", null, start); return true; }
                Advance(); AddToken(TokenKind.Star, "*", null, start); return true;

            case '/':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.SlashAssign, "/=", null, start); return true; }
                Advance(); AddToken(TokenKind.Slash, "/", null, start); return true;

            case '%':
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.PercentAssign, "%=", null, start); return true; }
                Advance(); AddToken(TokenKind.Percent, "%", null, start); return true;

            case '&':
                if (next == '&') { Advance(); Advance(); AddToken(TokenKind.AmpAmp, "&&", null, start); return true; }
                Advance(); AddToken(TokenKind.Ampersand, "&", null, start); return true;

            case '|':
                if (next == '|') { Advance(); Advance(); AddToken(TokenKind.PipePipe, "||", null, start); return true; }
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.BitOr, "|=", null, start); return true; }
                Advance(); AddToken(TokenKind.Pipe, "|", null, start); return true;

            case '?':
                if (next == '?') { Advance(); Advance(); AddToken(TokenKind.DoubleQuestion, "??", null, start); return true; }
                if (next == '.') { Advance(); Advance(); AddToken(TokenKind.NullCondMember, "?.", null, start); return true; }
                if (next == '[') { Advance(); Advance(); AddToken(TokenKind.NullCondIndex, "?[", null, start); return true; }
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.CoalesceAssign, "??=", null, start); return true; }
                Advance(); AddToken(TokenKind.Question, "?", null, start); return true;

            case '.':
                if (next == '.') {
                    // ADR-0050 §4: ..< 半开范围 / .. 闭范围
                    if (_pos + 2 < _source.Length && _source[_pos + 2] == '<')
                    { Advance(); Advance(); Advance(); AddToken(TokenKind.HalfOpenRange, "..<", null, start); return true; }
                    // 判断 .. 是范围运算符还是路径标识符：
                    // 前一个字符是数字/字母/}/)/] 时为范围运算符（1..10、$n..$m、(expr)..10），
                    // 否则为相对路径标识符（cd ..、cd ../foo）。
                    var prevChar = _pos > 0 ? _source[_pos - 1] : '\0';
                    if (char.IsLetterOrDigit(prevChar) || prevChar == '}' || prevChar == ')' || prevChar == ']')
                    { Advance(); Advance(); AddToken(TokenKind.Range, "..", null, start); return true; }
                    // 相对路径：读取完整片段（../foo、../../bar 等）作为 Identifier
                    Advance(); Advance(); // 消费 ..
                    var pathSb = new StringBuilder("..");
                    while (_pos < _source.Length && IsRelativePathChar(_source[_pos]))
                    { pathSb.Append(_source[_pos]); Advance(); }
                    AddToken(TokenKind.Identifier, pathSb.ToString(), pathSb.ToString(), start);
                    return true; }
                Advance(); AddToken(TokenKind.Dot, ".", null, start); return true;

            case ':':
                if (next == ':') { Advance(); Advance(); AddToken(TokenKind.DoubleColon, "::", null, start); return true; }
                // ADR-0050 §5.1 + PS label：':' 后跟标识符 → Label token（如 :outer）。
                // 用于循环标签声明（:outer for/while/foreach）和 break/continue :label。
                if (next is '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
                {
                    Advance(); // 消费 ':'
                    var sb = new StringBuilder(":");
                    while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                    { sb.Append(_source[_pos]); Advance(); }
                    AddToken(TokenKind.Label, sb.ToString(), sb.ToString().TrimStart(':'), start);
                    return true;
                }
                Advance(); AddToken(TokenKind.Colon, ":", null, start); return true;

            case '~':
                // ADR-0050 §2.1: ~= 通配符匹配 / ~regex 正则匹配。
                if (next == '=') { Advance(); Advance(); AddToken(TokenKind.TildeEquals, "~=", null, start); return true; }
                if (next == 'r' && _pos + 5 < _source.Length
                    && _source[_pos + 1] == 'r' && _source[_pos + 2] == 'e'
                    && _source[_pos + 3] == 'g' && _source[_pos + 4] == 'e'
                    && _source[_pos + 5] == 'x')
                {
                    // 确认后跟非标识符字符（避免误匹配 ~regexFoo）
                    var after = _pos + 6 < _source.Length ? _source[_pos + 6] : '\0';
                    if (!char.IsLetterOrDigit(after) && after != '_')
                    {
                        for (int i = 0; i < 6; i++) Advance();
                        AddToken(TokenKind.TildeRegex, "~regex", null, start);
                        return true;
                    }
                }
                Advance(); AddToken(TokenKind.BitNot, "~", null, start); return true;

            case '^':
                Advance(); AddToken(TokenKind.Caret, "^", null, start); return true;

            case ',':
                Advance(); AddToken(TokenKind.Comma, ",", null, start); return true;
        }

        return false;
    }

    private void LexSingleCharOperator(SourcePosition start)
    {
        var ch = _source[_pos];
        Advance();
        var kind = ch switch
        {
            '|' => TokenKind.Pipe,
            '&' => TokenKind.Ampersand,
            '.' => TokenKind.Dot,
            ':' => TokenKind.Colon,
            ',' => TokenKind.Comma,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '%' => TokenKind.Percent,
            '^' => TokenKind.Caret,
            '~' => TokenKind.BitNot,
            _ => TokenKind.End,
        };
        if (kind != TokenKind.End)
        {
            AddToken(kind, ch.ToString(), null, start);
        }
        // 未知字符：忽略（避免死循环）
    }
}
