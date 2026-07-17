namespace OpenShell.Completion;

/// <summary>
/// A single completion candidate returned by a completion source. Per ADR-0009.
/// Immutable; the same item shape is reused by the CLI tab handler and the GUI command palette.
/// </summary>
public sealed record CompletionItem(
    string DisplayText,
    string CompletionText,
    string? Description = null,
    CompletionKind Kind = CompletionKind.Text);

/// <summary>
/// Classifies a completion item so hosts can render it (icon, color) without re-parsing the text.
/// </summary>
public enum CompletionKind
{
    /// <summary>Unclassified text. Used as the default.</summary>
    Text = 0,

    /// <summary>A command full name such as get-childitem.</summary>
    Command,

    /// <summary>A parameter name such as -Path or -Recurse.</summary>
    Parameter,

    /// <summary>A file or directory path.</summary>
    Path,

    /// <summary>A variable reference such as $HOME.</summary>
    Variable,

    /// <summary>An alias name.</summary>
    Alias,

    /// <summary>An entry from the command history.</summary>
    History,
}

/// <summary>
/// Input to completion: the full line being edited and the cursor position within it.
/// Per ADR-0009.
/// </summary>
public sealed record CompletionContext(string Input, int CursorPosition);

/// <summary>
/// Pluggable completion abstraction per ADR-0009. Implemented by the aggregating provider
/// and consumed by both the CLI tab handler and the GUI command palette.
/// </summary>
public interface ICompletionProvider
{
    /// <summary>
    /// Return all completion candidates applicable to the given context, in priority order.
    /// Implementations must not have side effects and must tolerate incomplete or invalid input.
    /// </summary>
    IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context);
}
