using OpenShell.History;

namespace OpenShell.Completion.Sources;

/// <summary>
/// Completes the current input against command history at the command name position.
/// Per ADR-0009. Returns the most recent matching entries (newest first), deduplicated,
/// capped to a small number so the candidate list stays usable.
/// </summary>
public sealed class HistoryCompletionSource : ICompletionSource
{
    private const int MaxResults = 5;
    private readonly IHistoryService _history;

    public HistoryCompletionSource(IHistoryService history)
    {
        _history = history;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
    {
        var parsed = CompletionParser.Parse(context);
        if (!parsed.AtStart)
        {
            return Array.Empty<CompletionItem>();
        }

        var prefix = parsed.Token;
        var recent = _history.Recent;
        var results = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = recent.Count - 1; i >= 0 && results.Count < MaxResults; i--)
        {
            var entry = recent[i];
            if (!entry.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(entry.Command))
            {
                continue;
            }

            results.Add(new CompletionItem(
                entry.Command,
                entry.Command,
                null,
                CompletionKind.History));
        }

        return results;
    }
}
