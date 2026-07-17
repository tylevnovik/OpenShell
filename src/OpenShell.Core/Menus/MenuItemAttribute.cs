namespace OpenShell.Menus;

/// <summary>
/// Declares a menu contribution for a command class. Per ADR-0028 section 1.
/// A single command may declare multiple <c>[MenuItem]</c> attributes to
/// contribute to different menu paths (e.g. context menu and toolbar).
/// </summary>
/// <remarks>
/// <c>Path</c> is slash-separated, e.g. <c>context/copy</c> or <c>toolbar/refresh</c>.
/// Top-level prefixes are conventional: <c>context</c>, <c>toolbar</c>,
/// <c>menubar</c>, <c>sidebar</c>, <c>commandPalette</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class MenuItemAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance. The path may also be supplied via the
    /// <see cref="Path"/> init property when using named-argument attribute syntax.
    /// </summary>
    /// <param name="path">Optional slash-separated menu path.</param>
    public MenuItemAttribute(string path = "")
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>Slash-separated menu path, e.g. <c>context/copy</c>.</summary>
    public string Path { get; init; }

    /// <summary>Optional display label. If null, the label is derived from the path or command.</summary>
    public string? Label { get; init; }

    /// <summary>Optional i18n key (ADR-0035). When set, the GUI resolves the label through i18n.</summary>
    public string? LabelKey { get; init; }

    /// <summary>
    /// Optional When expression source. Null or empty means always visible.
    /// Per ADR-0028 section 2 and ADR-0027 When grammar.
    /// </summary>
    public string? When { get; init; }

    /// <summary>Sort order within siblings. Lower values come first. Defaults to zero.</summary>
    public int Order { get; init; }

    /// <summary>If true, this contribution represents a separator rather than a command.</summary>
    public bool IsSeparator { get; init; }

    /// <summary>
    /// If true, the command type implements <see cref="IDynamicMenuProvider"/> and its
    /// children are generated at runtime. Per ADR-0028 section 10.
    /// </summary>
    public bool IsDynamic { get; init; }
}

/// <summary>
/// Declares the icon asset path for a command class. Per ADR-0028 section 8.
/// The same icon is reused across all menu contributions of the command
/// (context menu, toolbar, etc.).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class IconAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance. The path may also be supplied via the
    /// <see cref="Path"/> init property when using named-argument attribute syntax.
    /// </summary>
    /// <param name="path">Optional asset path, e.g. <c>Icons/copy.svg</c>.</param>
    public IconAttribute(string path = "")
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>Asset path, e.g. <c>Icons/copy.svg</c>.</summary>
    public string Path { get; init; }
}
