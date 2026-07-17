using FluentAssertions;
using OpenShell.TestUtils;
using OpenShell.Themes;
using Xunit;

namespace OpenShell.Core.Tests.Themes;

/// <summary>
/// Unit tests for TomlThemeLoader. Per ADR-0027 section 1.
/// Uses a temp directory to isolate theme file IO from the real user home.
/// </summary>
public class TomlThemeLoaderTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    private const string ValidSolarizedToml = """
        name = 'solarized-dark'
        mode = 'dark'

        [colors]
        background = '#002b36'
        foreground = '#93a1a1'
        accent = '#268bd2'
        accentForeground = '#002b36'
        border = '#073642'
        muted = '#657b83'
        error = '#dc322f'
        warning = '#b58900'
        success = '#859900'
        directoryItem = '#268bd2'
        fileItem = '#93a1a1'
        selectedBackground = '#073642'

        [typography]
        fontFamily = 'Inter'
        fontSize = 14
        lineHeight = 20

        [metrics]
        spacingUnit = 8
        borderRadius = 4
        iconSize = 16
        """;

    [Fact]
    public void Load_ValidThemeFile_ReturnsTheme()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        _tempDir.CreateFile("solarized-dark.toml", ValidSolarizedToml);

        var theme = loader.Load("solarized-dark");

        theme.Should().NotBeNull();
        theme!.Name.Should().Be("solarized-dark");
        theme.Mode.Should().Be(ThemeMode.Dark);
        theme.Colors.Background.Should().Be("#002b36");
        theme.Colors.Foreground.Should().Be("#93a1a1");
        theme.Colors.Accent.Should().Be("#268bd2");
        theme.Typography.FontFamily.Should().Be("Inter");
        theme.Typography.FontSize.Should().Be(14);
        theme.Typography.LineHeight.Should().Be(20);
        theme.Metrics.SpacingUnit.Should().Be(8);
        theme.Metrics.BorderRadius.Should().Be(4);
        theme.Metrics.IconSize.Should().Be(16);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);

        var theme = loader.Load("does-not-exist");

        theme.Should().BeNull();
    }

    [Fact]
    public void Load_MalformedToml_ReturnsNull()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        _tempDir.CreateFile("bad.toml", "name = 'bad'\nthis is not valid toml = =");

        var theme = loader.Load("bad");

        theme.Should().BeNull();
    }

    [Fact]
    public void Load_MissingNameField_ReturnsNull()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        _tempDir.CreateFile("noname.toml", "mode = 'dark'\n[colors]\nbackground = '#000000'");

        var theme = loader.Load("noname");

        theme.Should().BeNull();
    }

    [Fact]
    public void Load_MissingMode_DefaultsToDark()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        _tempDir.CreateFile("no-mode.toml", """
            name = 'no-mode'

            [colors]
            background = '#000000'
            foreground = '#ffffff'
            accent = '#0000ff'
            accentForeground = '#ffffff'
            border = '#333333'
            muted = '#666666'
            error = '#ff0000'
            warning = '#ffaa00'
            success = '#00ff00'
            directoryItem = '#0000ff'
            fileItem = '#ffffff'
            selectedBackground = '#444444'

            [typography]
            fontFamily = 'Inter'
            fontSize = 14

            [metrics]
            spacingUnit = 8
            borderRadius = 4
            """);

        var theme = loader.Load("no-mode");

        theme.Should().NotBeNull();
        theme!.Mode.Should().Be(ThemeMode.Dark);
    }

    [Fact]
    public void LoadAll_MixedValidInvalid_SkipsInvalidFiles()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        _tempDir.CreateFile("valid-1.toml", ValidSolarizedToml);
        _tempDir.CreateFile("valid-2.toml", """
            name = 'another-theme'
            mode = 'light'

            [colors]
            background = '#ffffff'
            foreground = '#000000'
            accent = '#0066cc'
            accentForeground = '#ffffff'
            border = '#cccccc'
            muted = '#999999'
            error = '#ff0000'
            warning = '#ffaa00'
            success = '#00ff00'
            directoryItem = '#0066cc'
            fileItem = '#000000'
            selectedBackground = '#e0e0e0'

            [typography]
            fontFamily = 'Inter'
            fontSize = 14
            lineHeight = 20

            [metrics]
            spacingUnit = 8
            borderRadius = 4
            iconSize = 16
            """);
        _tempDir.CreateFile("malformed.toml", "this is = = not valid");
        _tempDir.CreateFile("no-name.toml", "mode = 'dark'");

        var themes = loader.LoadAll();

        themes.Should().HaveCount(2);
        themes.Select(t => t.Name).Should().Contain(new[] { "solarized-dark", "another-theme" });
    }

    [Fact]
    public void LoadAll_EmptyDirectory_ReturnsEmptyList()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);

        var themes = loader.LoadAll();

        themes.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_NonexistentDirectory_ReturnsEmptyList()
    {
        var missingDir = Path.Combine(_tempDir.FullPath, "does-not-exist");
        var loader = new TomlThemeLoader(missingDir);

        var themes = loader.LoadAll();

        themes.Should().BeEmpty();
    }

    [Fact]
    public void SaveAndReload_RoundTrip_PreservesAllFields()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        var original = new Theme(
            Name: "custom-test",
            Mode: ThemeMode.Light,
            Colors: new ThemeColors(
                Background: "#abc123",
                Foreground: "#fed456",
                Accent: "#0ff0a0",
                AccentForeground: "#000000",
                Border: "#cccccc",
                Muted: "#888888",
                Error: "#ff0000",
                Warning: "#ffaa00",
                Success: "#00ff00",
                DirectoryItem: "#0ff0a0",
                FileItem: "#fed456",
                SelectedBackground: "#333333"),
            Typography: new ThemeTypography("JetBrains Mono", 16, 24),
            Metrics: new ThemeMetrics(4, 8, 20),
            IconOverrides: new Dictionary<string, string>
            {
                ["folder"] = "folder-icon",
                ["file"] = "file-icon",
            });

        loader.Save(original);

        var loaded = loader.Load("custom-test");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be(original.Name);
        loaded.Mode.Should().Be(original.Mode);
        loaded.Colors.Should().Be(original.Colors);
        loaded.Typography.Should().Be(original.Typography);
        loaded.Metrics.Should().Be(original.Metrics);
        loaded.IconOverrides.Should().NotBeNull();
        loaded.IconOverrides!["folder"].Should().Be("folder-icon");
        loaded.IconOverrides!["file"].Should().Be("file-icon");
    }

    [Fact]
    public void SaveAndReload_RoundTrip_WithoutIconOverrides_PreservesAllFields()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        var original = new Theme(
            Name: "plain-theme",
            Mode: ThemeMode.HighContrast,
            Colors: new ThemeColors(
                "#000000", "#ffffff", "#ffff00", "#000000", "#ffffff", "#c0c0c0",
                "#ff0000", "#ffff00", "#00ff00", "#00ffff", "#ffffff", "#ffffff"),
            Typography: new ThemeTypography("Inter", 14, 20),
            Metrics: new ThemeMetrics(8, 4, 16),
            IconOverrides: null);

        loader.Save(original);
        var loaded = loader.Load("plain-theme");

        loaded.Should().NotBeNull();
        loaded.Should().Be(original);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var nestedDir = Path.Combine(_tempDir.FullPath, "themes", "nested");
        Directory.Exists(nestedDir).Should().BeFalse();

        var loader = new TomlThemeLoader(nestedDir);
        var theme = BuiltInThemes.Dark with { Name = "dir-test" };

        loader.Save(theme);

        Directory.Exists(nestedDir).Should().BeTrue();
        File.Exists(Path.Combine(nestedDir, "dir-test.toml")).Should().BeTrue();
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        var first = BuiltInThemes.Dark with { Name = "overwrite-test" };
        var second = BuiltInThemes.Light with { Name = "overwrite-test" };

        loader.Save(first);
        loader.Save(second);

        var loaded = loader.Load("overwrite-test");
        loaded.Should().NotBeNull();
        loaded!.Mode.Should().Be(ThemeMode.Light);
        loaded.Colors.Background.Should().Be("#FFFFFF");
    }

    [Fact]
    public void Load_WithIconOverrides_ParsesCorrectly()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        _tempDir.CreateFile("with-icons.toml", """
            name = 'with-icons'
            mode = 'dark'

            [colors]
            background = '#1e1e1e'
            foreground = '#e5e5e5'
            accent = '#4fc1ff'
            accentForeground = '#000000'
            border = '#3a3a3a'
            muted = '#808080'
            error = '#f48771'
            warning = '#cca700'
            success = '#89d185'
            directoryItem = '#4fc1ff'
            fileItem = '#e5e5e5'
            selectedBackground = '#264f78'

            [typography]
            fontFamily = 'Inter'
            fontSize = 14
            lineHeight = 20

            [metrics]
            spacingUnit = 8
            borderRadius = 4
            iconSize = 16

            [iconOverrides]
            folder = 'folder-glyph'
            file = 'file-glyph'
            """);

        var theme = loader.Load("with-icons");

        theme.Should().NotBeNull();
        theme!.IconOverrides.Should().NotBeNull();
        theme.IconOverrides!["folder"].Should().Be("folder-glyph");
        theme.IconOverrides!["file"].Should().Be("file-glyph");
    }

    public void Dispose() => _tempDir.Dispose();
}
