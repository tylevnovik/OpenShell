namespace OpenShell.Completion;

/// <summary>
/// Tokenizes completion input so individual sources can decide whether they apply.
/// Per ADR-0009. The parser is intentionally tolerant: it never throws on odd input,
/// it just reports the token under the cursor and whether that token is the command name.
/// </summary>
public sealed record ParsedCompletion
{
    /// <summary>
    /// The text from the last whitespace boundary up to the cursor position.
    /// Empty when the cursor sits immediately after whitespace.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// True when the cursor is at the first token position (the command name stage).
    /// When true, <see cref="CurrentCommandName"/> is null.
    /// </summary>
    public required bool AtStart { get; init; }

    /// <summary>
    /// The command name (first token) when the cursor is past the command name, otherwise null.
    /// Sources use this to resolve the active command for parameter completion.
    /// </summary>
    public string? CurrentCommandName { get; init; }

    /// <summary>
    /// The text preceding the current token. Empty when at the start of the line.
    /// </summary>
    public required string Prefix { get; init; }
}

/// <summary>
/// Static helper that parses a <see cref="CompletionContext"/> into a <see cref="ParsedCompletion"/>.
/// Per ADR-0009. Each <see cref="ICompletionSource"/> uses this to avoid re-implementing tokenization.
/// </summary>
public static class CompletionParser
{
    /// <summary>Parse the context into token, position, and current command metadata.</summary>
    public static ParsedCompletion Parse(CompletionContext context)
    {
        var line = context.Input ?? "";
        var cursor = Math.Clamp(context.CursorPosition, 0, line.Length);

        if (cursor == 0)
        {
            return new ParsedCompletion
            {
                Token = "",
                AtStart = true,
                CurrentCommandName = null,
                Prefix = "",
            };
        }

        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(line[start - 1]))
        {
            start--;
        }

        var token = line.Substring(start, cursor - start);
        var atStart = start == 0;
        var prefix = atStart ? "" : line[..start];
        var commandName = atStart ? null : FirstToken(prefix);

        return new ParsedCompletion
        {
            Token = token,
            AtStart = atStart,
            CurrentCommandName = commandName,
            Prefix = prefix,
        };
    }

    private static string? FirstToken(string prefix)
    {
        var parts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 1 ? parts[0] : null;
    }
}
