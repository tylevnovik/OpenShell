using OpenShell.Variables;

namespace OpenShell.Completion.Sources;

/// <summary>
/// Completes variable names when the token under the cursor starts with a dollar sign.
/// Per ADR-0009. Matches by name prefix (case-insensitive) after stripping the dollar sign.
/// </summary>
public sealed class VariableCompletionSource : ICompletionSource
{
    private readonly IVariableRegistry _variables;

    public VariableCompletionSource(IVariableRegistry variables)
    {
        _variables = variables;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
    {
        var parsed = CompletionParser.Parse(context);
        if (!parsed.Token.StartsWith("$", StringComparison.Ordinal))
        {
            return Array.Empty<CompletionItem>();
        }

        var prefix = parsed.Token[1..];
        var results = new List<CompletionItem>();
        foreach (var pair in _variables.List())
        {
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var text = "$" + pair.Key;
                results.Add(new CompletionItem(text, text, null, CompletionKind.Variable));
            }
        }

        return results;
    }
}
