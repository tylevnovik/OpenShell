using OpenShell.Commands;

namespace OpenShell.Completion.Sources;

/// <summary>
/// Completes parameter names for the active command when the token under the cursor starts
/// with a hyphen. Per ADR-0009. The active command is parsed from the first token of the input.
/// </summary>
public sealed class ParameterCompletionSource : ICompletionSource
{
    private readonly ICommandRegistry _commands;

    public ParameterCompletionSource(ICommandRegistry commands)
    {
        _commands = commands;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
    {
        var parsed = CompletionParser.Parse(context);
        if (parsed.AtStart || !parsed.Token.StartsWith("-", StringComparison.Ordinal))
        {
            return Array.Empty<CompletionItem>();
        }

        if (parsed.CurrentCommandName is null)
        {
            return Array.Empty<CompletionItem>();
        }

        var descriptor = _commands.Resolve(parsed.CurrentCommandName);
        if (descriptor is null)
        {
            return Array.Empty<CompletionItem>();
        }

        var bare = parsed.Token.TrimStart('-');
        var results = new List<CompletionItem>();
        foreach (var parameter in descriptor.Parameters)
        {
            if (parameter.Name.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CompletionItem(
                    "-" + parameter.Name,
                    "-" + parameter.Name,
                    parameter.Mandatory ? "Required" : null,
                    CompletionKind.Parameter));
            }

            foreach (var alias in parameter.Aliases)
            {
                var trimmed = alias.TrimStart('-');
                if (trimmed.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CompletionItem(alias, alias, null, CompletionKind.Parameter));
                }
            }
        }

        return results;
    }
}
