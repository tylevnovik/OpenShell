namespace OpenShell.Completion;

/// <summary>
/// A single completion source handling one kind of candidate. Per ADR-0009.
/// Sources are composable: the <see cref="AggregatingCompletionProvider"/> runs all registered
/// sources and concatenates their results. A source returns an empty list when it does not
/// apply to the current context.
/// </summary>
public interface ICompletionSource
{
    /// <summary>
    /// Return the completion candidates this source contributes for the given context.
    /// Return an empty list when the source does not apply.
    /// </summary>
    IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context);
}
