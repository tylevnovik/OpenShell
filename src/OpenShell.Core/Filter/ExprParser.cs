using System.Globalization;
using System.Text;

namespace OpenShell.Filter;

/// <summary>
/// Filter DSL Lexer。Per ADR-0012 §2.
/// 把字符串表达式切成 <see cref="Token"/> 流，供 <see cref="ExprParser"/> 递归下降解析。
/// </summary>
public sealed class Lexer
{
    private readonly string _source;
    private int _pos;

    public Lexer(string source)
    {
        _source = source ?? "";
        _pos = 0;
    }

    public Token Next()
    {
        SkipWhitespace();
        if (_pos >= _source.Length)
            return new(TokenKind.End, "", null, _pos);

        var start = _pos;
        var ch = _source[_pos];

        // 1. 字符串字面量："..." 与 '...'
        if (ch == '"' || ch == '\'')
            return LexString(ch, start);

        // 2. 数字字面量（含 0x / 0b / KB/MB/GB/TB 单位）
        if (char.IsDigit(ch))
        {
            if (TryLexDate(start, out var dateToken))
                return dateToken;
            return LexNumber(start);
        }

        // 3. 负号开头：可能是负数，也可能是 -and/-or/-not
        if (ch == '-' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
            return LexNumber(start);

        // 4. 标识符 / 关键字
        if (char.IsLetter(ch) || ch == '_')
            return LexIdentifier(start);

        // 5. 标点 / 运算符
        return LexPunctuation(start);
    }

    private void SkipWhitespace()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
            _pos++;
    }

    private Token LexString(char quote, int start)
    {
        _pos++; // consume opening quote
        var sb = new StringBuilder();
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == quote)
            {
                _pos++; // consume closing quote
                var text = sb.ToString();
                // 双引号字符串支持转义；单引号原样（PowerShell 风格）。
                // 但我们已在 LexString 内部处理了转义，所以这里返回最终字符串。
                return new(TokenKind.String, text, text, start);
            }
            if (quote == '"' && c == '\\')
            {
                _pos++;
                if (_pos >= _source.Length) break;
                var esc = _source[_pos];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    '\'' => '\'',
                    '/' => '/',
                    _ => esc,
                });
                _pos++;
            }
            else
            {
                sb.Append(c);
                _pos++;
            }
        }
        throw new FilterParseException($"unterminated string starting at position {start}", start);
    }

    private Token LexNumber(int start)
    {
        var sb = new StringBuilder();
        // 负号
        if (_pos < _source.Length && _source[_pos] == '-')
        {
            sb.Append('-');
            _pos++;
        }

        // 0x / 0b 前缀
        if (_pos + 1 < _source.Length && _source[_pos] == '0'
            && (_source[_pos + 1] == 'x' || _source[_pos + 1] == 'X'))
        {
            sb.Append("0x");
            _pos += 2;
            var hex = new StringBuilder();
            while (_pos < _source.Length && IsHexDigit(_source[_pos]))
            {
                hex.Append(_source[_pos]);
                _pos++;
            }
            var value = long.Parse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new(TokenKind.Number, sb.ToString() + hex, value, start);
        }
        if (_pos + 1 < _source.Length && _source[_pos] == '0'
            && (_source[_pos + 1] == 'b' || _source[_pos + 1] == 'B'))
        {
            _pos += 2;
            var bin = new StringBuilder();
            while (_pos < _source.Length && (_source[_pos] == '0' || _source[_pos] == '1'))
            {
                bin.Append(_source[_pos]);
                _pos++;
            }
            var value = Convert.ToInt64(bin.ToString(), 2);
            return new(TokenKind.Number, "0b" + bin, value, start);
        }

        // 十进制整数 / 小数部分
        while (_pos < _source.Length && char.IsDigit(_source[_pos]))
        {
            sb.Append(_source[_pos]);
            _pos++;
        }
        var hasDecimal = false;
        if (_pos < _source.Length && _source[_pos] == '.'
            && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            hasDecimal = true;
            sb.Append('.');
            _pos++;
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
            {
                sb.Append(_source[_pos]);
                _pos++;
            }
        }

        // 数字单位（KB/MB/GB/TB，1024 进制，大小写不敏感）
        var unitMultiplier = TryReadUnit(out var unitText);
        if (unitMultiplier is { } mult)
        {
            var numText = sb.ToString();
            if (hasDecimal)
            {
                var d = double.Parse(numText, CultureInfo.InvariantCulture);
                return new(TokenKind.Number, numText + unitText, (long)(d * mult), start);
            }
            else
            {
                var l = long.Parse(numText, CultureInfo.InvariantCulture);
                return new(TokenKind.Number, numText + unitText, l * mult, start);
            }
        }

        // 普通数字
        if (hasDecimal)
        {
            var d = double.Parse(sb.ToString(), CultureInfo.InvariantCulture);
            return new(TokenKind.Number, sb.ToString(), d, start);
        }
        var lv = long.Parse(sb.ToString(), CultureInfo.InvariantCulture);
        return new(TokenKind.Number, sb.ToString(), lv, start);
    }

    /// <summary>尝试读取数字开头的 ISO 日期或日期时间字面量。</summary>
    private bool TryLexDate(int start, out Token token)
    {
        token = default;
        if (_source.Length - start < 10
            || !char.IsDigit(_source[start])
            || !char.IsDigit(_source[start + 1])
            || !char.IsDigit(_source[start + 2])
            || !char.IsDigit(_source[start + 3])
            || _source[start + 4] != '-'
            || !char.IsDigit(_source[start + 5])
            || !char.IsDigit(_source[start + 6])
            || _source[start + 7] != '-'
            || !char.IsDigit(_source[start + 8])
            || !char.IsDigit(_source[start + 9]))
        {
            return false;
        }

        var end = start + 10;
        if (end < _source.Length && (_source[end] == 'T' || _source[end] == 't'))
        {
            end++;
            while (end < _source.Length && IsDateTimeContinuation(_source[end]))
                end++;
        }

        if (end < _source.Length && (char.IsLetterOrDigit(_source[end]) || _source[end] == '_'))
            return false;

        var text = _source[start..end];
        if (!TryReadDate(text, out var date))
            return false;

        _pos = end;
        token = new Token(TokenKind.Date, text, date, start);
        return true;
    }

    /// <summary>尝试读取数字单位（KB/MB/GB/TB）。返回乘数，未读到返回 null。</summary>
    private long? TryReadUnit(out string text)
    {
        text = "";
        if (_pos + 1 >= _source.Length) return null;
        var u1 = char.ToUpperInvariant(_source[_pos]);
        var u2 = char.ToUpperInvariant(_source[_pos + 1]);
        // 检查后续 2 字符是 KB/MB/GB/TB，并且再下一个字符不是字母（避免与标识符冲突）
        if (u2 == 'B')
        {
            var mult = u1 switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024,
                'G' => 1024L * 1024 * 1024,
                'T' => 1024L * 1024 * 1024 * 1024,
                _ => 0L,
            };
            if (mult > 0)
            {
                // 检查后面不是字母（避免 sizeM 之类被错误吞掉）
                if (_pos + 2 < _source.Length && (char.IsLetterOrDigit(_source[_pos + 2]) || _source[_pos + 2] == '_'))
                    return null;
                text = $"{(char)u1}{(char)u2}";
                _pos += 2;
                return mult;
            }
        }
        return null;
    }

    private Token LexIdentifier(int start)
    {
        var sb = new StringBuilder();
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
        {
            sb.Append(_source[_pos]);
            _pos++;
        }
        var name = sb.ToString();

        var lower = name.ToLowerInvariant();
        var kind = lower switch
        {
            "and" => TokenKind.And,
            "or" => TokenKind.Or,
            "not" => TokenKind.Not,
            "in" => TokenKind.In,
            "contains" => TokenKind.Contains,
            "startswith" => TokenKind.StartsWith,
            "endswith" => TokenKind.EndsWith,
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "null" => TokenKind.Null,
            _ => TokenKind.Identifier,
        };
        return new(kind, name, name, start);
    }

    /// <summary>识别日期字面量。Per ADR-0012 §4：2026-01-01、2026-01-01T12:00:00Z 等。</summary>
    private static bool TryReadDate(string s, out DateTimeOffset date)
    {
        if (s.Length >= 10
            && char.IsDigit(s[0]) && char.IsDigit(s[1]) && char.IsDigit(s[2]) && char.IsDigit(s[3])
            && s[4] == '-'
            && char.IsDigit(s[5]) && char.IsDigit(s[6])
            && s[7] == '-'
            && char.IsDigit(s[8]) && char.IsDigit(s[9]))
        {
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out date);
        }
        date = default;
        return false;
    }

    private Token LexPunctuation(int start)
    {
        var ch = _source[_pos];
        switch (ch)
        {
            case '(': _pos++; return new(TokenKind.LParen, "(", null, start);
            case ')': _pos++; return new(TokenKind.RParen, ")", null, start);
            case '[': _pos++; return new(TokenKind.LBracket, "[", null, start);
            case ']': _pos++; return new(TokenKind.RBracket, "]", null, start);
            case ',': _pos++; return new(TokenKind.Comma, ",", null, start);
            case '=': _pos++; return new(TokenKind.Eq, "=", null, start);
            case '<':
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '=') { _pos++; return new(TokenKind.Le, "<=", null, start); }
                return new(TokenKind.Lt, "<", null, start);
            case '>':
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '=') { _pos++; return new(TokenKind.Ge, ">=", null, start); }
                return new(TokenKind.Gt, ">", null, start);
            case '~':
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '=') { _pos++; return new(TokenKind.Glob, "~=", null, start); }
                throw new FilterParseException($"expected '=' after '~' at position {start}", start, "~");
            case '!':
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '=')
                {
                    _pos++;
                    return new(TokenKind.Ne, "!=", null, start);
                }
                if (_pos < _source.Length && _source[_pos] == '~')
                {
                    _pos++;
                    if (_pos < _source.Length && _source[_pos] == '=') { _pos++; return new(TokenKind.NotGlob, "!~=", null, start); }
                    throw new FilterParseException($"expected '=' after '!~' at position {start}", start, "!~");
                }
                // C-style ! 逻辑非
                return new(TokenKind.Not, "!", null, start);
            case '&':
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '&') { _pos++; return new(TokenKind.And, "&&", null, start); }
                throw new FilterParseException($"expected '&&' at position {start}", start, "&");
            case '|':
                _pos++;
                if (_pos < _source.Length && _source[_pos] == '|') { _pos++; return new(TokenKind.Or, "||", null, start); }
                throw new FilterParseException($"expected '||' at position {start}", start, "|");
            case '-':
                // -and / -or / -not （PowerShell 风格）
                _pos++;
                var opSb = new StringBuilder();
                while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                {
                    opSb.Append(_source[_pos]);
                    _pos++;
                }
                var opText = opSb.ToString().ToLowerInvariant();
                return opText switch
                {
                    "and" => new(TokenKind.And, "-" + opText, null, start),
                    "or" => new(TokenKind.Or, "-" + opText, null, start),
                    "not" => new(TokenKind.Not, "-" + opText, null, start),
                    _ => throw new FilterParseException(
                        $"unknown operator '-{opText}' at position {start}", start, "-" + opText),
                };
            default:
                throw new FilterParseException(
                    $"unexpected character '{ch}' at position {start}", start, ch.ToString());
        }
    }

    private static bool IsHexDigit(char ch) =>
        char.IsDigit(ch) || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

    private static bool IsDateTimeContinuation(char ch) =>
        char.IsDigit(ch) || ch is ':' or '.' or '+' or '-' or 'Z' or 'z';
}

/// <summary>Token 类型。Per ADR-0012 §2.</summary>
public enum TokenKind
{
    Number,
    String,
    Date,
    Identifier,

    // 逻辑运算符
    And,
    Or,
    Not,

    // 比较运算符
    Eq,         // =
    Ne,         // !=
    Lt,         // <
    Gt,         // >
    Le,         // <=
    Ge,         // >=
    Glob,       // ~=
    NotGlob,    // !~=
    In,         // in
    Contains,   // contains
    StartsWith, // startswith
    EndsWith,   // endswith

    // 字面量关键字
    True,
    False,
    Null,

    // 标点
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,

    End,
}

/// <summary>不可变 Token。Position 是源表达式中的字符偏移（0-based）。</summary>
public readonly record struct Token(TokenKind Kind, string Text, object? Value, int Position);

/// <summary>
/// Filter DSL 递归下降 Parser。Per ADR-0012 §2.
/// <para>
/// 文法（运算符优先级：-not &gt; 比较运算符 &gt; -and &gt; -or）：
/// <code>
/// expr      := orExpr
/// orExpr    := andExpr ( OR andExpr )*
/// andExpr   := notExpr ( AND notExpr )*
/// notExpr   := NOT notExpr | comparison
/// comparison:= primary ( OP primary )?
/// primary   := property | literal | arrayLiteral | '(' expr ')'
/// arrayLiteral := '[' ( literal (',' literal)* )? ']'
/// property  := IDENT
/// literal   := NUMBER [UNIT] | STRING | TRUE | FALSE | 'null' | DATE
/// </code>
/// </para>
/// </summary>
public sealed class ExprParser
{
    private readonly Lexer _lexer;
    private Token _current;

    private ExprParser(string source)
    {
        _lexer = new Lexer(source);
        _current = _lexer.Next();
    }

    /// <summary>解析表达式，返回 AST 根节点。</summary>
    public static ExprAst Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new FilterParseException("expression is empty", 0);
        var parser = new ExprParser(source);
        var expr = parser.ParseOr();
        if (parser._current.Kind != TokenKind.End)
            throw new FilterParseException(
                $"unexpected token '{parser._current.Text}' at position {parser._current.Position}",
                parser._current.Position, parser._current.Text);
        return expr;
    }

    /// <summary>解析逗号分隔的投影列表（用于 select name, size as bytes）。</summary>
    public static IReadOnlyList<ProjectionExpr> ParseProjectionList(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new FilterParseException("projection list is empty", 0);
        var parser = new ExprParser(source);
        var list = new List<ProjectionExpr>();
        do
        {
            list.Add(parser.ParseProjection());
            if (parser._current.Kind == TokenKind.Comma)
            {
                parser.Advance();
                continue;
            }
            break;
        } while (true);

        if (parser._current.Kind != TokenKind.End)
            throw new FilterParseException(
                $"unexpected token '{parser._current.Text}' at position {parser._current.Position}",
                parser._current.Position, parser._current.Text);
        return list;
    }

    private ProjectionExpr ParseProjection()
    {
        var expr = ParseOr();
        string? alias = null;
        // 支持 `as name` 别名
        if (_current.Kind == TokenKind.Identifier
            && string.Equals(_current.Text, "as", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            if (_current.Kind != TokenKind.Identifier)
                throw new FilterParseException(
                    $"expected alias name after 'as' at position {_current.Position}",
                    _current.Position, _current.Text);
            alias = _current.Text;
            Advance();
        }
        return new ProjectionExpr(expr, alias);
    }

    private ExprAst ParseOr()
    {
        var left = ParseAnd();
        while (_current.Kind == TokenKind.Or)
        {
            Advance();
            var right = ParseAnd();
            left = new LogicalExpr(left, LogicalOp.Or, right);
        }
        return left;
    }

    private ExprAst ParseAnd()
    {
        var left = ParseNot();
        while (_current.Kind == TokenKind.And)
        {
            Advance();
            var right = ParseNot();
            left = new LogicalExpr(left, LogicalOp.And, right);
        }
        return left;
    }

    private ExprAst ParseNot()
    {
        if (_current.Kind == TokenKind.Not)
        {
            Advance();
            var inner = ParseNot();
            return new NotExpr(inner);
        }
        return ParseComparison();
    }

    private ExprAst ParseComparison()
    {
        var left = ParsePrimary();
        if (TryGetComparisonOp(_current.Kind, out var op))
        {
            var opToken = _current;
            Advance();
            // 右侧允许 arrayLiteral 或单 literal
            var right = ParsePrimary();
            if (left is not PropertyAccessExpr paLeft)
                throw new FilterParseException(
                    $"left side of comparison must be a property at position {opToken.Position}",
                    opToken.Position, opToken.Text);
            if (right is not LiteralExpr litRight)
                throw new FilterParseException(
                    $"right side of comparison must be a literal at position {opToken.Position}",
                    opToken.Position, opToken.Text);
            return new ComparisonExpr(paLeft, op!.Value, litRight);
        }
        return left;
    }

    private static bool TryGetComparisonOp(TokenKind kind, out ComparisonOp? op)
    {
        op = kind switch
        {
            TokenKind.Eq => ComparisonOp.Eq,
            TokenKind.Ne => ComparisonOp.Ne,
            TokenKind.Lt => ComparisonOp.Lt,
            TokenKind.Gt => ComparisonOp.Gt,
            TokenKind.Le => ComparisonOp.Le,
            TokenKind.Ge => ComparisonOp.Ge,
            TokenKind.Glob => ComparisonOp.Glob,
            TokenKind.NotGlob => ComparisonOp.NotGlob,
            TokenKind.In => ComparisonOp.In,
            TokenKind.Contains => ComparisonOp.Contains,
            TokenKind.StartsWith => ComparisonOp.StartsWith,
            TokenKind.EndsWith => ComparisonOp.EndsWith,
            _ => null,
        };
        return op is not null;
    }

    private ExprAst ParsePrimary()
    {
        switch (_current.Kind)
        {
            case TokenKind.LParen:
                {
                    Advance();
                    var inner = ParseOr();
                    Expect(TokenKind.RParen, "expected ')' after expression");
                    return inner;
                }
            case TokenKind.LBracket:
                return ParseArrayLiteral();
            case TokenKind.String:
                {
                    var tok = _current;
                    Advance();
                    return new LiteralExpr(tok.Value, LiteralKind.String);
                }
            case TokenKind.Date:
                {
                    var tok = _current;
                    Advance();
                    return new LiteralExpr(tok.Value, LiteralKind.Date);
                }
            case TokenKind.Number:
                {
                    var tok = _current;
                    Advance();
                    return new LiteralExpr(tok.Value, LiteralKind.Number);
                }
            case TokenKind.True:
                Advance();
                return new LiteralExpr(true, LiteralKind.Boolean);
            case TokenKind.False:
                Advance();
                return new LiteralExpr(false, LiteralKind.Boolean);
            case TokenKind.Null:
                Advance();
                return new LiteralExpr(null, LiteralKind.Null);
            case TokenKind.Identifier:
                {
                    var name = _current.Text;
                    Advance();
                    return new PropertyAccessExpr(name);
                }
            default:
                throw new FilterParseException(
                    $"unexpected token '{_current.Text}' at position {_current.Position}",
                    _current.Position, _current.Text);
        }
    }

    private LiteralExpr ParseArrayLiteral()
    {
        // 已知当前 token 是 '['
        Advance();
        var items = new List<object?>();
        if (_current.Kind != TokenKind.RBracket)
        {
            while (true)
            {
                var item = ParsePrimary();
                if (item is LiteralExpr lit)
                    items.Add(lit.Value);
                else
                    throw new FilterParseException(
                        $"array element must be a literal at position {_current.Position}",
                        _current.Position, _current.Text);

                if (_current.Kind == TokenKind.Comma)
                {
                    Advance();
                    continue;
                }
                break;
            }
        }
        Expect(TokenKind.RBracket, "expected ']' after array literal");
        // array 用 LiteralKind.Number 占位（evaluator 通过 Value 类型识别数组）。
        return new LiteralExpr(items.ToArray(), LiteralKind.Number);
    }

    private void Advance() => _current = _lexer.Next();

    private void Expect(TokenKind kind, string errorMessage)
    {
        if (_current.Kind != kind)
            throw new FilterParseException(
                $"{errorMessage}, got '{_current.Text}' at position {_current.Position}",
                _current.Position, _current.Text);
        Advance();
    }
}
