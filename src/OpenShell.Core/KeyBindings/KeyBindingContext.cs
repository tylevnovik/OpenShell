namespace OpenShell.KeyBindings;

/// <summary>
/// Runtime context used to evaluate When expressions for keybindings. Per ADR-0027 section 2.
/// Flattened to a dictionary whose keys match When expression identifiers
/// (focus, selectedItemType, provider, modal).
/// </summary>
public sealed class KeyBindingContext
{
    /// <summary>Focused element: pane, tree, locationbox, console.</summary>
    public string FocusedElement { get; set; } = "";

    /// <summary>Type of the selected item: file, directory, archive.</summary>
    public string SelectedItemType { get; set; } = "";

    /// <summary>Current provider identifier.</summary>
    public string CurrentProvider { get; set; } = "";

    /// <summary>True when a modal dialog is open.</summary>
    public bool IsModalOpen { get; set; }

    /// <summary>
    /// Flatten to a dictionary for WhenExpression evaluation.
    /// </summary>
    /// <returns>Read-only context dictionary.</returns>
    public IReadOnlyDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>
    {
        ["focus"] = FocusedElement,
        ["selectedItemType"] = SelectedItemType,
        ["provider"] = CurrentProvider,
        ["modal"] = IsModalOpen,
    };
}
