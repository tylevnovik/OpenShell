namespace OpenShell.Themes;

/// <summary>
/// Theme mode enumeration. Per ADR-0027 section 1.
/// Determines the overall appearance category of a theme.
/// </summary>
public enum ThemeMode
{
    /// <summary>Light theme with bright background.</summary>
    Light,

    /// <summary>Dark theme with dim background.</summary>
    Dark,

    /// <summary>Follow the operating system preference at runtime.</summary>
    System,

    /// <summary>High contrast accessibility theme.</summary>
    HighContrast,
}

/// <summary>
/// Color palette for a theme. Per ADR-0027 section 1.
/// All values are hex color strings (e.g. "#1E1E1E").
/// </summary>
/// <param name="Background">Base background color.</param>
/// <param name="Foreground">Default text color.</param>
/// <param name="Accent">Accent / highlight color for links and focused elements.</param>
/// <param name="AccentForeground">Text color drawn on top of the accent color.</param>
/// <param name="Border">Border and separator color.</param>
/// <param name="Muted">Dimmed text color for secondary content.</param>
/// <param name="Error">Error indicator color.</param>
/// <param name="Warning">Warning indicator color.</param>
/// <param name="Success">Success indicator color.</param>
/// <param name="DirectoryItem">Color for directory items in listings.</param>
/// <param name="FileItem">Color for file items in listings.</param>
/// <param name="SelectedBackground">Background color for selected items.</param>
public sealed record ThemeColors(
    string Background,
    string Foreground,
    string Accent,
    string AccentForeground,
    string Border,
    string Muted,
    string Error,
    string Warning,
    string Success,
    string DirectoryItem,
    string FileItem,
    string SelectedBackground);

/// <summary>
/// Typography settings for a theme. Per ADR-0027 section 1.
/// </summary>
/// <param name="FontFamily">CSS-like font family name.</param>
/// <param name="FontSize">Base font size in pixels.</param>
/// <param name="LineHeight">Line height in pixels.</param>
public sealed record ThemeTypography(string FontFamily, int FontSize, int LineHeight);

/// <summary>
/// Layout metrics for a theme. Per ADR-0027 section 1.
/// </summary>
/// <param name="SpacingUnit">Base spacing unit in pixels; derived paddings and margins are multiples of this.</param>
/// <param name="BorderRadius">Corner radius in pixels for boxes and controls.</param>
/// <param name="IconSize">Icon size in pixels.</param>
public sealed record ThemeMetrics(int SpacingUnit, int BorderRadius, int IconSize);

/// <summary>
/// A complete GUI theme definition. Per ADR-0027 section 1.
/// Combines colors, typography, metrics, and optional icon overrides.
/// </summary>
/// <param name="Name">Unique theme name. Lookups are case-insensitive.</param>
/// <param name="Mode">Theme mode category.</param>
/// <param name="Colors">Color palette.</param>
/// <param name="Typography">Typography settings.</param>
/// <param name="Metrics">Layout metrics.</param>
/// <param name="IconOverrides">Optional map of icon name to replacement glyph; null if none.</param>
public sealed record Theme(
    string Name,
    ThemeMode Mode,
    ThemeColors Colors,
    ThemeTypography Typography,
    ThemeMetrics Metrics,
    IReadOnlyDictionary<string, string>? IconOverrides = null);
