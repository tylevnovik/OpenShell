namespace OpenShell.Menus;

/// <summary>
/// Resolved menu contribution produced by scanning command types for
/// <c>[MenuItem]</c> attributes. Per ADR-0028 section 1.
/// </summary>
public sealed record MenuItemContribution
{
    /// <summary>Slash-separated menu path, e.g. <c>context/copy</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Command full name (e.g. <c>copy-item</c>) the contribution invokes.</summary>
    public required string CommandId { get; init; }

    /// <summary>Display label. May be null if the GUI derives it from the command.</summary>
    public string? Label { get; init; }

    /// <summary>Optional i18n key (ADR-0035).</summary>
    public string? LabelKey { get; init; }

    /// <summary>When expression source. Null or empty means always visible.</summary>
    public string? When { get; init; }

    /// <summary>Sort order within siblings. Lower values come first.</summary>
    public int Order { get; init; }

    /// <summary>If true, the contribution represents a separator.</summary>
    public bool IsSeparator { get; init; }

    /// <summary>
    /// If true, the command type implements <see cref="IDynamicMenuProvider"/>
    /// and its children are generated at runtime.
    /// </summary>
    public bool IsDynamic { get; init; }

    /// <summary>Optional icon asset path, e.g. <c>Icons/copy.svg</c>.</summary>
    public string? IconPath { get; init; }
}

/// <summary>
/// Snapshot of the current selection state used by the When expression
/// evaluator. Per ADR-0028 section 2.
/// </summary>
public sealed record SelectionInfo
{
    /// <summary>Number of selected items.</summary>
    public int Count { get; init; }

    /// <summary>True if every selected item is a directory.</summary>
    public bool AllDirectories { get; init; }

    /// <summary>True if every selected item is a file.</summary>
    public bool AllFiles { get; init; }

    /// <summary>True if the selection contains at least one archive item.</summary>
    public bool ContainsArchive { get; init; }

    /// <summary>True if exactly one item is selected.</summary>
    public bool SingleItem => Count == 1;
}

/// <summary>
/// Context passed to <see cref="IMenuService.GetVisibleNodes"/> and
/// <see cref="IDynamicMenuProvider.Generate"/>. Per ADR-0028 section 2.
/// </summary>
public sealed record MenuContext(
    string FocusedElement = "",
    SelectionInfo? Selection = null,
    string CurrentLocation = "",
    string CurrentProvider = "")
{
    /// <summary>
    /// Renders the context to a flat dictionary of dotted keys suitable for
    /// the <c>WhenExpression</c> evaluator (e.g. <c>selected.count</c>, <c>focus</c>).
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>
    {
        ["focus"] = FocusedElement,
        ["selected.count"] = Selection?.Count ?? 0,
        ["selected.allDirectories"] = Selection?.AllDirectories ?? false,
        ["selected.allFiles"] = Selection?.AllFiles ?? false,
        ["selected.containsArchive"] = Selection?.ContainsArchive ?? false,
        ["selected.singleItem"] = Selection?.SingleItem ?? false,
        ["provider"] = CurrentProvider,
    };
}
