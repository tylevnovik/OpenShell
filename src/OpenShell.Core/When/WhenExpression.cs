namespace OpenShell.When;

/// <summary>
/// Compiled When expression for keybinding / menu context filtering.
/// Per ADR-0027 section 2 (KeyBindingContext) and ADR-0028 section 2 (MenuContext).
/// </summary>
/// <remarks>
/// Grammar (simple recursive descent): or-and-not-primary-condition.
/// The colon operator is sugar for equality (ADR-0027 style focus:pane).
/// Context keys may be dotted (e.g. selected.count); the evaluator looks up the
/// full dotted key directly in the supplied context dictionary.
/// Empty / null / whitespace expression always evaluates to true.
/// </remarks>
public sealed class WhenExpression
{
    private readonly Node _root;
    private readonly string? _source;

    private WhenExpression(Node root, string? source)
    {
        _root = root;
        _source = source;
    }

    /// <summary>The original source text (null if the expression was empty).</summary>
    public string? Source => _source;

    /// <summary>True if this expression is empty (always-true).</summary>
    public bool IsEmpty => _root is AlwaysTrueNode;

    /// <summary>
    /// Parse a When expression. Null / empty / whitespace yields an always-true expression.
    /// </summary>
    /// <exception cref="WhenParseException">Thrown when the expression is malformed.</exception>
    public static WhenExpression Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new WhenExpression(AlwaysTrueNode.Instance, null);
        }

        var source = expression!;
        var tokens = new Tokenizer(source).Tokenize();
        var parser = new Parser(tokens);
        var root = parser.ParseExpression();
        parser.ExpectEnd();
        return new WhenExpression(root, source);
    }

    /// <summary>
    /// Evaluate the expression against a flat context dictionary whose keys may be dotted
    /// (e.g. <c>"selected.count"</c>, <c>"focus"</c>).
    /// </summary>
    public bool Evaluate(IReadOnlyDictionary<string, object?> context)
    {
        return _root.Evaluate(context);
    }

    // ---- AST nodes --------------------------------------------------------

    private abstract class Node
    {
        public abstract bool Evaluate(IReadOnlyDictionary<string, object?> context);
    }

    private sealed class AlwaysTrueNode : Node
    {
        public static readonly AlwaysTrueNode Instance = new();
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context) => true;
    }

    private sealed class OrNode(Node left, Node right) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
            => left.Evaluate(context) || right.Evaluate(context);
    }

    private sealed class AndNode(Node left, Node right) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
            => left.Evaluate(context) && right.Evaluate(context);
    }

    private sealed class NotNode(Node inner) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
            => !inner.Evaluate(context);
    }

    private sealed class ConditionNode(string key, string op, Token valueToken) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
        {
            context.TryGetValue(key, out var raw);
            return op switch
            {
                ":" or "==" => Equals(raw, valueToken),
                "!=" => !Equals(raw, valueToken),
                ">" => CompareNumbers(raw, valueToken) > 0,
                "<" => CompareNumbers(raw, valueToken) < 0,
                ">=" => CompareNumbers(raw, valueToken) >= 0,
                "<=" => CompareNumbers(raw, valueToken) <= 0,
                _ => false,
            };
        }

        private static bool Equals(object? raw, Token valueToken)
        {
            if (raw is null) return string.Equals(valueToken.Text, "null", StringComparison.OrdinalIgnoreCase);
            var rawStr = raw switch
            {
                bool b => b ? "true" : "false",
                _ => raw.ToString() ?? "",
            };
            return string.Equals(rawStr, valueToken.Text, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareNumbers(object? raw, Token valueToken)
        {
            if (raw is null) return -1;
            if (!double.TryParse(raw.ToString(), out var rawNum)) return -1;
            if (!double.TryParse(valueToken.Text, out var valNum)) return -1;
            return rawNum.CompareTo(valNum);
        }
    }

    private sealed class TruthyNode(string key) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
        {
            if (!context.TryGetValue(key, out var raw) || raw is null) return false;
            return raw switch
            {
                bool b => b,
                int i => i != 0,
                long l => l != 0,
                double d => d != 0,
                string s => !string.IsNullOrEmpty(s) && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
        }
    }

    // ---- Tokenizer --------------------------------------------------------

    private enum TokenKind { Identifier, Number, String, Op, LParen, RParen, End }

    private sealed class Token(TokenKind kind, string text)
    {
        public TokenKind Kind { get; } = kind;
        public string Text { get; } = text;
    }

    private sealed class Tokenizer(string source)
    {
        private int _pos;

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            while (_pos < source.Length)
            {
                var c = source[_pos];
                if (char.IsWhiteSpace(c)) { _pos++; continue; }
                if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(")); _pos++; continue; }
                if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")")); _pos++; continue; }
                if (c == '!')
                {
                    if (Peek(1) == '=') { tokens.Add(new Token(TokenKind.Op, "!=")); _pos += 2; }
                    else { tokens.Add(new Token(TokenKind.Op, "!")); _pos++; }
                    continue;
                }
                if (c == '=' && Peek(1) == '=') { tokens.Add(new Token(TokenKind.Op, "==")); _pos += 2; continue; }
                if (c == '>' && Peek(1) == '=') { tokens.Add(new Token(TokenKind.Op, ">=")); _pos += 2; continue; }
                if (c == '<' && Peek(1) == '=') { tokens.Add(new Token(TokenKind.Op, "<=")); _pos += 2; continue; }
                if (c == '>') { tokens.Add(new Token(TokenKind.Op, ">")); _pos++; continue; }
                if (c == '<') { tokens.Add(new Token(TokenKind.Op, "<")); _pos++; continue; }
                if (c == ':') { tokens.Add(new Token(TokenKind.Op, ":")); _pos++; continue; }
                if (c == '&' && Peek(1) == '&') { tokens.Add(new Token(TokenKind.Op, "&&")); _pos += 2; continue; }
                if (c == '|' && Peek(1) == '|') { tokens.Add(new Token(TokenKind.Op, "||")); _pos += 2; continue; }
                if (c == '"' || c == '\'') { tokens.Add(ReadString(c)); continue; }
                if (char.IsDigit(c) || (c == '-' && _pos + 1 < source.Length && char.IsDigit(source[_pos + 1])))
                {
                    tokens.Add(ReadNumber()); continue;
                }
                if (char.IsLetter(c) || c == '_') { tokens.Add(ReadIdentifier()); continue; }
                throw new WhenParseException($"Unexpected character '{c}' at position {_pos}.");
            }
            tokens.Add(new Token(TokenKind.End, ""));
            return tokens;
        }

        private char Peek(int offset) => _pos + offset < source.Length ? source[_pos + offset] : '\0';

        private Token ReadString(char quote)
        {
            _pos++; // skip opening quote
            var start = _pos;
            while (_pos < source.Length && source[_pos] != quote) _pos++;
            if (_pos >= source.Length) throw new WhenParseException("Unterminated string literal.");
            var text = source.Substring(start, _pos - start);
            _pos++; // skip closing quote
            return new Token(TokenKind.String, text);
        }

        private Token ReadNumber()
        {
            var start = _pos;
            if (source[_pos] == '-') _pos++;
            while (_pos < source.Length && (char.IsDigit(source[_pos]) || source[_pos] == '.')) _pos++;
            return new Token(TokenKind.Number, source.Substring(start, _pos - start));
        }

        private Token ReadIdentifier()
        {
            var start = _pos;
            while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_' || source[_pos] == '.'))
                _pos++;
            return new Token(TokenKind.Identifier, source.Substring(start, _pos - start));
        }
    }

    // ---- Parser -----------------------------------------------------------

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        public Node ParseExpression() => ParseOr();

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Current.Kind == TokenKind.Op && Current.Text == "||")
            {
                _pos++;
                left = new OrNode(left, ParseAnd());
            }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (Current.Kind == TokenKind.Op && Current.Text == "&&")
            {
                _pos++;
                left = new AndNode(left, ParseNot());
            }
            return left;
        }

        private Node ParseNot()
        {
            if (Current.Kind == TokenKind.Op && Current.Text == "!")
            {
                _pos++;
                return new NotNode(ParseNot());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            if (Current.Kind == TokenKind.LParen)
            {
                _pos++;
                var node = ParseOr();
                if (Current.Kind != TokenKind.RParen)
                    throw new WhenParseException("Expected ')' in When expression.");
                _pos++;
                return node;
            }

            if (Current.Kind == TokenKind.Identifier)
            {
                var ident = Current.Text;
                // Boolean literal
                if (string.Equals(ident, "true", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    return AlwaysTrueNode.Instance;
                }
                if (string.Equals(ident, "false", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    return new NotNode(AlwaysTrueNode.Instance);
                }

                _pos++;
                // Condition: ident op value ?
                if (Current.Kind == TokenKind.Op && IsComparisonOp(Current.Text))
                {
                    var op = Current.Text;
                    _pos++;
                    if (Current.Kind != TokenKind.String && Current.Kind != TokenKind.Number
                        && Current.Kind != TokenKind.Identifier)
                    {
                        throw new WhenParseException($"Expected a value after '{op}' in When expression.");
                    }
                    var value = Current;
                    _pos++;
                    return new ConditionNode(ident, op, value);
                }
                // Truthy
                return new TruthyNode(ident);
            }

            throw new WhenParseException(
                $"Unexpected token '{Current.Text}' at position {_pos} in When expression.");
        }

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
                throw new WhenParseException($"Unexpected trailing token '{Current.Text}' in When expression.");
        }

        private static bool IsComparisonOp(string text)
            => text is ":" or "==" or "!=" or ">" or "<" or ">=" or "<=";

        private Token Current => tokens[_pos];
    }
}

/// <summary>Thrown when a When expression fails to parse.</summary>
public sealed class WhenParseException(string message) : Exception(message);
