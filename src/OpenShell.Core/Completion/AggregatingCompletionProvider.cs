namespace OpenShell.Completion;

/// <summary>
/// Combines multiple <see cref="ICompletionSource"/> instances into a single
/// <see cref="ICompletionProvider"/>. Per ADR-0009. Each source contributes only the
/// candidates it owns; the aggregator concatenates them in registration order.
/// </summary>
public sealed class AggregatingCompletionProvider : ICompletionProvider
{
    private readonly IReadOnlyList<ICompletionSource> _sources;

    public AggregatingCompletionProvider(IEnumerable<ICompletionSource> sources)
    {
        _sources = sources.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
        => _sources.SelectMany(s => s.GetCompletions(context)).ToList();
}
