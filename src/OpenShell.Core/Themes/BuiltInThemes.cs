namespace OpenShell.Themes;

/// <summary>
/// Built-in theme definitions. Per ADR-0027 section 1.
/// Provides Light, Dark, and HighContrast themes with realistic color palettes.
/// These are always available regardless of user theme files.
/// </summary>
public static class BuiltInThemes
{
    /// <summary>Default typography shared by all built-in themes: Inter font, size 14, line height 20.</summary>
    public static readonly ThemeTypography DefaultTypography = new("Inter", 14, 20);

    /// <summary>Default metrics shared by all built-in themes: spacing 8, radius 4, icon 16.</summary>
    public static readonly ThemeMetrics DefaultMetrics = new(8, 4, 16);

    /// <summary>Light theme: white background with dark foreground.</summary>
    public static readonly Theme Light = new(
        Name: "light",
        Mode: ThemeMode.Light,
        Colors: new ThemeColors(
            Background: "#FFFFFF",
            Foreground: "#1E1E1E",
            Accent: "#0066CC",
            AccentForeground: "#FFFFFF",
            Border: "#E5E5E5",
            Muted: "#6C6C6C",
            Error: "#D13438",
            Warning: "#CA5010",
            Success: "#107C10",
            DirectoryItem: "#0066CC",
            FileItem: "#1E1E1E",
            SelectedBackground: "#CCE4F7"),
        Typography: DefaultTypography,
        Metrics: DefaultMetrics);

    /// <summary>Dark theme: dark background with light foreground. This is the default active theme.</summary>
    public static readonly Theme Dark = new(
        Name: "dark",
        Mode: ThemeMode.Dark,
        Colors: new ThemeColors(
            Background: "#1E1E1E",
            Foreground: "#E5E5E5",
            Accent: "#4FC1FF",
            AccentForeground: "#000000",
            Border: "#3A3A3A",
            Muted: "#808080",
            Error: "#F48771",
            Warning: "#CCA700",
            Success: "#89D185",
            DirectoryItem: "#4FC1FF",
            FileItem: "#E5E5E5",
            SelectedBackground: "#264F78"),
        Typography: DefaultTypography,
        Metrics: DefaultMetrics);

    /// <summary>High contrast theme: pure black and white for maximum accessibility.</summary>
    public static readonly Theme HighContrast = new(
        Name: "highcontrast",
        Mode: ThemeMode.HighContrast,
        Colors: new ThemeColors(
            Background: "#000000",
            Foreground: "#FFFFFF",
            Accent: "#FFFF00",
            AccentForeground: "#000000",
            Border: "#FFFFFF",
            Muted: "#C0C0C0",
            Error: "#FF0000",
            Warning: "#FFFF00",
            Success: "#00FF00",
            DirectoryItem: "#00FFFF",
            FileItem: "#FFFFFF",
            SelectedBackground: "#FFFFFF"),
        Typography: DefaultTypography,
        Metrics: DefaultMetrics);

    /// <summary>All built-in themes in canonical order: Light, Dark, HighContrast.</summary>
    public static IReadOnlyList<Theme> All => new[] { Light, Dark, HighContrast };
}
