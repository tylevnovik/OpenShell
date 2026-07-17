using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Themes;

/// <summary>
/// Loads and saves theme TOML files. Per ADR-0027 section 1.
/// Each theme is stored as a single toml file inside the themes directory.
/// Invalid files are skipped gracefully (ADR constraint: never block startup on a bad theme file).
/// </summary>
public sealed class TomlThemeLoader
{
    private readonly string _themesDir;

    /// <summary>
    /// Construct a TomlThemeLoader.
    /// </summary>
    /// <param name="themesDir">Directory containing theme toml files. Defaults to <see cref="OpenShell.OpenShellPaths.ThemesDir"/>.</param>
    public TomlThemeLoader(string? themesDir = null)
    {
        _themesDir = themesDir ?? OpenShellPaths.ThemesDir;
    }

    /// <summary>
    /// Load a single theme by name. The file is resolved as the theme name plus the toml extension
    /// inside the configured themes directory.
    /// </summary>
    /// <param name="name">Theme name, matching the file name without extension.</param>
    /// <returns>The loaded theme, or null if the file does not exist or fails to parse.</returns>
    public Theme? Load(string name)
    {
        var path = Path.Combine(_themesDir, name + ".toml");
        return LoadFromFile(path);
    }

    /// <summary>
    /// Load all valid themes from the themes directory. Invalid files are skipped.
    /// </summary>
    /// <returns>List of successfully parsed themes. Empty if the directory does not exist.</returns>
    public IReadOnlyList<Theme> LoadAll()
    {
        if (!Directory.Exists(_themesDir))
        {
            return Array.Empty<Theme>();
        }

        var result = new List<Theme>();
        foreach (var file in Directory.EnumerateFiles(_themesDir, "*.toml"))
        {
            var theme = LoadFromFile(file);
            if (theme is not null)
            {
                result.Add(theme);
            }
        }
        return result;
    }

    /// <summary>
    /// Serialize a theme to TOML and write it to the themes directory as the theme name plus toml.
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <param name="theme">Theme to save.</param>
    public void Save(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Directory.CreateDirectory(_themesDir);
        var path = Path.Combine(_themesDir, theme.Name + ".toml");
        var toml = SerializeTheme(theme);
        File.WriteAllText(path, toml);
    }

    private Theme? LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[warn] failed to read theme '{path}': {ex.Message}");
            return null;
        }

        return ParseTheme(text);
    }

    private static Theme? ParseTheme(string text)
    {
        TomlTable root;
        try
        {
            root = Toml.ToModel(text);
        }
        catch (Tomlyn.TomlException ex)
        {
            Console.Error.WriteLine($"[warn] failed to parse theme: {ex.Message}");
            return null;
        }

        var name = TryGetString(root, "name");
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var mode = ParseMode(TryGetString(root, "mode"));

        var colors = root.TryGetValue("colors", out var cv) && cv is TomlTable ct
            ? ParseColors(ct)
            : BuiltInThemes.Dark.Colors;

        var typography = root.TryGetValue("typography", out var tv) && tv is TomlTable tt
            ? ParseTypography(tt)
            : BuiltInThemes.DefaultTypography;

        var metrics = root.TryGetValue("metrics", out var mv) && mv is TomlTable mt
            ? ParseMetrics(mt)
            : BuiltInThemes.DefaultMetrics;

        IReadOnlyDictionary<string, string>? iconOverrides = null;
        if (root.TryGetValue("iconOverrides", out var iv) && iv is TomlTable it)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in it)
            {
                if (kv.Value is string s)
                {
                    dict[kv.Key] = s;
                }
            }
            if (dict.Count > 0)
            {
                iconOverrides = dict;
            }
        }

        return new Theme(name!, mode, colors, typography, metrics, iconOverrides);
    }

    private static string? TryGetString(TomlTable table, string key)
        => table.TryGetValue(key, out var v) ? v as string : null;

    private static int? TryGetInt(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var v)) return null;
        if (v is long l) return (int)l;
        if (v is int i) return i;
        return null;
    }

    private static ThemeMode ParseMode(string? mode)
    {
        return mode?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            "system" => ThemeMode.System,
            "highcontrast" or "high-contrast" => ThemeMode.HighContrast,
            _ => ThemeMode.Dark,
        };
    }

    private static ThemeColors ParseColors(TomlTable t)
    {
        var d = BuiltInThemes.Dark.Colors;
        return new ThemeColors(
            Background: TryGetString(t, "background") ?? d.Background,
            Foreground: TryGetString(t, "foreground") ?? d.Foreground,
            Accent: TryGetString(t, "accent") ?? d.Accent,
            AccentForeground: TryGetString(t, "accentForeground") ?? d.AccentForeground,
            Border: TryGetString(t, "border") ?? d.Border,
            Muted: TryGetString(t, "muted") ?? d.Muted,
            Error: TryGetString(t, "error") ?? d.Error,
            Warning: TryGetString(t, "warning") ?? d.Warning,
            Success: TryGetString(t, "success") ?? d.Success,
            DirectoryItem: TryGetString(t, "directoryItem") ?? d.DirectoryItem,
            FileItem: TryGetString(t, "fileItem") ?? d.FileItem,
            SelectedBackground: TryGetString(t, "selectedBackground") ?? d.SelectedBackground);
    }

    private static ThemeTypography ParseTypography(TomlTable t)
    {
        var d = BuiltInThemes.DefaultTypography;
        return new ThemeTypography(
            FontFamily: TryGetString(t, "fontFamily") ?? d.FontFamily,
            FontSize: TryGetInt(t, "fontSize") ?? d.FontSize,
            LineHeight: TryGetInt(t, "lineHeight") ?? d.LineHeight);
    }

    private static ThemeMetrics ParseMetrics(TomlTable t)
    {
        var d = BuiltInThemes.DefaultMetrics;
        return new ThemeMetrics(
            SpacingUnit: TryGetInt(t, "spacingUnit") ?? d.SpacingUnit,
            BorderRadius: TryGetInt(t, "borderRadius") ?? d.BorderRadius,
            IconSize: TryGetInt(t, "iconSize") ?? d.IconSize);
    }

    private static string SerializeTheme(Theme theme)
    {
        var root = new TomlTable
        {
            ["name"] = theme.Name,
            ["mode"] = ModeToString(theme.Mode),
        };

        var colors = new TomlTable
        {
            ["background"] = theme.Colors.Background,
            ["foreground"] = theme.Colors.Foreground,
            ["accent"] = theme.Colors.Accent,
            ["accentForeground"] = theme.Colors.AccentForeground,
            ["border"] = theme.Colors.Border,
            ["muted"] = theme.Colors.Muted,
            ["error"] = theme.Colors.Error,
            ["warning"] = theme.Colors.Warning,
            ["success"] = theme.Colors.Success,
            ["directoryItem"] = theme.Colors.DirectoryItem,
            ["fileItem"] = theme.Colors.FileItem,
            ["selectedBackground"] = theme.Colors.SelectedBackground,
        };
        root["colors"] = colors;

        var typography = new TomlTable
        {
            ["fontFamily"] = theme.Typography.FontFamily,
            ["fontSize"] = (long)theme.Typography.FontSize,
            ["lineHeight"] = (long)theme.Typography.LineHeight,
        };
        root["typography"] = typography;

        var metrics = new TomlTable
        {
            ["spacingUnit"] = (long)theme.Metrics.SpacingUnit,
            ["borderRadius"] = (long)theme.Metrics.BorderRadius,
            ["iconSize"] = (long)theme.Metrics.IconSize,
        };
        root["metrics"] = metrics;

        if (theme.IconOverrides is not null && theme.IconOverrides.Count > 0)
        {
            var io = new TomlTable();
            foreach (var kv in theme.IconOverrides)
            {
                io[kv.Key] = kv.Value;
            }
            root["iconOverrides"] = io;
        }

        return Toml.FromModel(root);
    }

    private static string ModeToString(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        ThemeMode.System => "system",
        ThemeMode.HighContrast => "highcontrast",
        _ => "dark",
    };
}
