using OpenShell.Commands;

namespace OpenShell.Completion.Sources;

/// <summary>
/// Completes command full names and their built-in aliases at the command name position.
/// Per ADR-0009. Matches by full-name prefix and alias prefix (case-insensitive).
/// </summary>
public sealed class CommandCompletionSource : ICompletionSource
{
    private readonly ICommandRegistry _commands;

    public CommandCompletionSource(ICommandRegistry commands)
    {
        _commands = commands;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
    {
        var parsed = CompletionParser.Parse(context);
        if (!parsed.AtStart)
        {
            return Array.Empty<CompletionItem>();
        }

        var token = parsed.Token;
        var results = new List<CompletionItem>();
        foreach (var descriptor in _commands.Registered)
        {
            if (descriptor.FullName.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CompletionItem(
                    descriptor.FullName,
                    descriptor.FullName,
                    descriptor.Description,
                    CompletionKind.Command));
            }

            foreach (var alias in descriptor.Aliases)
            {
                if (alias.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CompletionItem(
                        alias,
                        alias,
                        "Alias for " + descriptor.FullName,
                        CompletionKind.Alias));
                }
            }
        }

        return results;
    }
}
