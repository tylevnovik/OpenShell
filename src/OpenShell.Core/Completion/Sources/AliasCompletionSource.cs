using OpenShell.Commands;

namespace OpenShell.Completion.Sources;

/// <summary>
/// Completes user-defined alias and function names at the command name position.
/// Per ADR-0009. Built-in command aliases are handled by <see cref="CommandCompletionSource"/>;
/// this source covers the user-global, project, and session aliases and functions surfaced
/// through <see cref="IAliasRegistry"/>.
/// </summary>
public sealed class AliasCompletionSource : ICompletionSource
{
    private readonly IAliasRegistry _aliases;

    public AliasCompletionSource(IAliasRegistry aliases)
    {
        _aliases = aliases;
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
        foreach (var alias in _aliases.List())
        {
            if (alias.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CompletionItem(
                    alias.Name,
                    alias.Name,
                    alias.Description,
                    CompletionKind.Alias));
            }
        }

        return results;
    }
}
