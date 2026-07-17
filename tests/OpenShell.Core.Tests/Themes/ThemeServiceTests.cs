using FluentAssertions;
using OpenShell.TestUtils;
using OpenShell.Themes;
using Xunit;

namespace OpenShell.Core.Tests.Themes;

/// <summary>
/// Unit tests for ThemeService. Per ADR-0027 section 1.
/// Uses a temp directory for the theme loader to avoid polluting the real user home.
/// </summary>
public class ThemeServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    private ThemeService CreateService()
    {
        var loader = new TomlThemeLoader(_tempDir.FullPath);
        return new ThemeService(loader);
    }

    [Fact]
    public void Current_DefaultsToDark()
    {
        var svc = CreateService();

        svc.Current.Should().Be(BuiltInThemes.Dark);
        svc.Current.Name.Should().Be("dark");
        svc.Current.Mode.Should().Be(ThemeMode.Dark);
    }

    [Fact]
    public void Available_IncludesThreeBuiltins()
    {
        var svc = CreateService();

        svc.Available.Should().HaveCount(3);
        svc.Available.Select(t => t.Name).Should().Contain(new[] { "light", "dark", "highcontrast" });
    }

    [Fact]
    public void Available_BuiltinsComeFirst()
    {
        var svc = CreateService();

        svc.Available[0].Name.Should().Be("light");
        svc.Available[1].Name.Should().Be("dark");
        svc.Available[2].Name.Should().Be("highcontrast");
    }

    [Fact]
    public void Apply_ThemeInstance_UpdatesCurrentAndFiresChanged()
    {
        var svc = CreateService();
        var fired = new List<Theme>();
        var sub = svc.Changed.Subscribe(t => fired.Add(t));

        svc.Apply(BuiltInThemes.Light);

        svc.Current.Should().Be(BuiltInThemes.Light);
        fired.Should().ContainSingle();
        fired[0].Should().Be(BuiltInThemes.Light);
        sub.Dispose();
    }

    [Fact]
    public void Apply_Name_CaseInsensitive_UpdatesCurrent()
    {
        var svc = CreateService();

        svc.Apply("LIGHT");

        svc.Current.Should().Be(BuiltInThemes.Light);
    }

    [Fact]
    public void Apply_Name_Lowercase_UpdatesCurrent()
    {
        var svc = CreateService();

        svc.Apply("highcontrast");

        svc.Current.Should().Be(BuiltInThemes.HighContrast);
    }

    [Fact]
    public void Apply_UnknownName_ThrowsArgumentException()
    {
        var svc = CreateService();

        var act = () => svc.Apply("nonexistent-theme");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*nonexistent-theme*");
    }

    [Fact]
    public void Apply_ThemeInstance_Null_ThrowsArgumentNullException()
    {
        var svc = CreateService();

        var act = () => svc.Apply((Theme)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Apply_Name_Null_ThrowsArgumentNullException()
    {
        var svc = CreateService();

        var act = () => svc.Apply((string)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Available_IncludesUserThemesFromLoader()
    {
        _tempDir.CreateFile("custom.toml", """
            name = 'custom'
            mode = 'dark'

            [colors]
            background = '#1a1a2e'
            foreground = '#e0e0e0'
            accent = '#e94560'
            accentForeground = '#ffffff'
            border = '#16213e'
            muted = '#707070'
            error = '#ff4444'
            warning = '#ffcc00'
            success = '#44ff44'
            directoryItem = '#e94560'
            fileItem = '#e0e0e0'
            selectedBackground = '#0f3460'

            [typography]
            fontFamily = 'Fira Code'
            fontSize = 13
            lineHeight = 19

            [metrics]
            spacingUnit = 6
            borderRadius = 6
            iconSize = 14
            """);

        var svc = CreateService();

        svc.Available.Should().HaveCount(4);
        svc.Available.Select(t => t.Name).Should().Contain("custom");
        var custom = svc.Available.First(t => t.Name == "custom");
        custom.Colors.Accent.Should().Be("#e94560");
        custom.Typography.FontFamily.Should().Be("Fira Code");
        custom.Metrics.BorderRadius.Should().Be(6);
    }

    [Fact]
    public void Available_BuiltinsBeforeUserThemes()
    {
        _tempDir.CreateFile("user.toml", """
            name = 'user'
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

        var svc = CreateService();

        svc.Available.Should().HaveCount(4);
        // Built-ins first.
        svc.Available[0].Name.Should().Be("light");
        svc.Available[1].Name.Should().Be("dark");
        svc.Available[2].Name.Should().Be("highcontrast");
        // User theme last.
        svc.Available[3].Name.Should().Be("user");
    }

    [Fact]
    public void Apply_Name_FiresChangedObservable()
    {
        var svc = CreateService();
        var fired = new List<Theme>();
        var sub = svc.Changed.Subscribe(t => fired.Add(t));

        svc.Apply("light");

        fired.Should().ContainSingle();
        fired[0].Should().Be(BuiltInThemes.Light);
        sub.Dispose();
    }

    [Fact]
    public void Apply_Twice_FiresChangedTwice()
    {
        var svc = CreateService();
        var fired = new List<Theme>();
        var sub = svc.Changed.Subscribe(t => fired.Add(t));

        svc.Apply(BuiltInThemes.Light);
        svc.Apply(BuiltInThemes.HighContrast);

        fired.Should().HaveCount(2);
        fired[0].Should().Be(BuiltInThemes.Light);
        fired[1].Should().Be(BuiltInThemes.HighContrast);
        svc.Current.Should().Be(BuiltInThemes.HighContrast);
        sub.Dispose();
    }

    [Fact]
    public void Apply_UserThemeByName_UpdatesCurrent()
    {
        _tempDir.CreateFile("custom.toml", """
            name = 'custom'
            mode = 'dark'

            [colors]
            background = '#1a1a2e'
            foreground = '#e0e0e0'
            accent = '#e94560'
            accentForeground = '#ffffff'
            border = '#16213e'
            muted = '#707070'
            error = '#ff4444'
            warning = '#ffcc00'
            success = '#44ff44'
            directoryItem = '#e94560'
            fileItem = '#e0e0e0'
            selectedBackground = '#0f3460'

            [typography]
            fontFamily = 'Fira Code'
            fontSize = 13
            lineHeight = 19

            [metrics]
            spacingUnit = 6
            borderRadius = 6
            iconSize = 14
            """);

        var svc = CreateService();

        svc.Apply("CUSTOM");

        svc.Current.Name.Should().Be("custom");
        svc.Current.Colors.Accent.Should().Be("#e94560");
    }

    public void Dispose() => _tempDir.Dispose();
}
