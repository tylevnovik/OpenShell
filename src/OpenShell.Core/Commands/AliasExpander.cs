using System.Text;

namespace OpenShell.Commands;

/// <summary>
/// Static alias expander. Per ADR-0024 §2, alias expansion is a text replacement that
/// re-parses the result. Expansion is token-based: a complete token (between whitespace
/// or at the start of the input) is replaced only if it matches an alias name exactly.
/// Tokens inside quotes and option tokens (starting with <c>-</c>) are never expanded.
/// Functions take precedence over aliases: if a token names a function, it is left
/// untouched (the dispatcher resolves it later).
/// Multi-level expansion is bounded by <see cref="MaxDepth"/> to guarantee termination
/// even if the registry somehow contains a cycle (the registry also self-checks on
/// mutation, but the bound is defence-in-depth).
/// </summary>
public static class AliasExpander
{
    /// <summary>Maximum expansion depth. Per ADR-0024: at most 10 levels of aliasing.</summary>
    public const int MaxDepth = 10;

    /// <summary>
    /// Expand all alias tokens in <paramref name="input"/> using <paramref name="aliases"/>.
    /// Returns the expanded command string (possibly equal to <paramref name="input"/>
    /// when no aliases match). Quoted tokens are preserved verbatim; option tokens
    /// (starting with <c>-</c>) are skipped; function names are skipped (function
    /// resolution is the dispatcher's responsibility).
    /// </summary>
    /// <param name="input">Raw command line typed by the user.</param>
    /// <param name="aliases">Alias registry for resolution.</param>
    /// <returns>Expanded command string, or <paramref name="input"/> unchanged if no expansion occurred.</returns>
    public static string Expand(string input, IAliasRegistry aliases)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        ArgumentNullException.ThrowIfNull(aliases);

        var current = input;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            var tokens = Tokenize(current);
            if (tokens.Count == 0) break;

            var expanded = false;
            // D-319: 别名展开仅作用于命令位置（首个非空白 token）。
            // 参数位置的 token 不应被展开为别名（如 rm -r dir 中的 dir 是路径参数，
            // 不应被展开为 Get-ChildItem）。与 PowerShell 语义一致。
            if (tokens.Count > 0)
            {
                var token = tokens[0];
                if (!token.IsQuoted
                    && token.Value.Length > 0
                    && token.Value[0] != '-'
                    && token.Value[0] != '"'
                    // Functions take precedence over aliases: skip expansion if the token
                    // resolves to a function. The dispatcher will invoke the function.
                    && aliases.ResolveFunction(token.Value) is null)
                {
                    var alias = aliases.Resolve(token.Value);
                    if (alias is not null)
                    {
                        var replacement = Tokenize(alias.Command);
                        tokens.RemoveAt(0);
                        tokens.InsertRange(0, replacement);
                        expanded = true;
                    }
                }
            }

            if (!expanded) break;
            current = Join(tokens);
        }

        return current;
    }

    /// <summary>
    /// Tokenize a command line into whitespace-separated tokens, respecting double quotes.
    /// Quoted tokens retain their <see cref="Token.IsQuoted"/> flag so that <see cref="Join"/>
    /// can restore the quotes. Single quotes are treated as ordinary characters (the
    /// shell's primary quote form is the double quote, matching ADR-0008).
    /// </summary>
    /// <param name="input">Command line to tokenize.</param>
    /// <returns>List of tokens with their quote status.</returns>
    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(input)) return tokens;

        var sb = new StringBuilder();
        var inQuote = false;
        var hasToken = false;

        for (int i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '"')
            {
                // D-316: 保留引号字符在 token 值中，避免 Join 时丢失引号导致
                // 带特殊字符的路径（如 "C:\Users\foo" ; pwd）被错误重组。
                sb.Append(ch);
                inQuote = !inQuote;
                hasToken = true;
                continue;
            }
            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (hasToken)
                {
                    tokens.Add(new Token(sb.ToString(), IsQuoted: false));
                    sb.Clear();
                    hasToken = false;
                }
                continue;
            }
            sb.Append(ch);
            hasToken = true;
        }
        if (hasToken) tokens.Add(new Token(sb.ToString(), IsQuoted: inQuote));
        return tokens;
    }

    /// <summary>
    /// Rejoin tokens, restoring double quotes around tokens that were originally quoted.
    /// Unquoted tokens are emitted verbatim; tokens are separated by a single space.
    /// </summary>
    /// <param name="tokens">Tokens to join.</param>
    /// <returns>Joined command string.</returns>
    private static string Join(List<Token> tokens)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            // D-316: 引号已保留在 token 值中，直接输出即可。
            sb.Append(tokens[i].Value);
        }
        return sb.ToString();
    }

    private readonly record struct Token(string Value, bool IsQuoted);
}
