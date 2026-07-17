using FluentAssertions;
using OpenShell.Themes;
using Xunit;

namespace OpenShell.Core.Tests.Themes;

/// <summary>
/// Unit tests for theme record equality and immutability. Per ADR-0027 section 1.
/// </summary>
public class ThemeRecordsTests
{
    [Fact]
    public void ThemeColors_Equal_WhenSameValues()
    {
        var a = new ThemeColors(
            "#000000", "#ffffff", "#0066cc", "#ffffff", "#cccccc", "#999999",
            "#ff0000", "#ffaa00", "#00ff00", "#0066cc", "#ffffff", "#333333");
        var b = new ThemeColors(
            "#000000", "#ffffff", "#0066cc", "#ffffff", "#cccccc", "#999999",
            "#ff0000", "#ffaa00", "#00ff00", "#0066cc", "#ffffff", "#333333");

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ThemeColors_NotEqual_WhenDifferentValues()
    {
        var a = new ThemeColors(
            "#000000", "#ffffff", "#0066cc", "#ffffff", "#cccccc", "#999999",
            "#ff0000", "#ffaa00", "#00ff00", "#0066cc", "#ffffff", "#333333");
        var b = a with { Background = "#ffffff" };

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void ThemeTypography_Equal_WhenSameValues()
    {
        var a = new ThemeTypography("Inter", 14, 20);
        var b = new ThemeTypography("Inter", 14, 20);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void ThemeTypography_NotEqual_WhenDifferentFontSize()
    {
        var a = new ThemeTypography("Inter", 14, 20);
        var b = a with { FontSize = 16 };

        a.Should().NotBe(b);
    }

    [Fact]
    public void ThemeMetrics_Equal_WhenSameValues()
    {
        var a = new ThemeMetrics(8, 4, 16);
        var b = new ThemeMetrics(8, 4, 16);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Theme_WithNullIconOverrides_Equal_WhenSameFields()
    {
        var colors = BuiltInThemes.Dark.Colors;
        var typo = BuiltInThemes.DefaultTypography;
        var metrics = BuiltInThemes.DefaultMetrics;
        var a = new Theme("test", ThemeMode.Dark, colors, typo, metrics, null);
        var b = new Theme("test", ThemeMode.Dark, colors, typo, metrics, null);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Theme_NotEqual_WhenDifferentName()
    {
        var colors = BuiltInThemes.Dark.Colors;
        var a = new Theme("alpha", ThemeMode.Dark, colors, BuiltInThemes.DefaultTypography, BuiltInThemes.DefaultMetrics);
        var b = a with { Name = "beta" };

        a.Should().NotBe(b);
    }

    [Fact]
    public void Theme_NotEqual_WhenDifferentMode()
    {
        var colors = BuiltInThemes.Dark.Colors;
        var a = new Theme("test", ThemeMode.Dark, colors, BuiltInThemes.DefaultTypography, BuiltInThemes.DefaultMetrics);
        var b = a with { Mode = ThemeMode.Light };

        a.Should().NotBe(b);
    }

    [Fact]
    public void ThemeMode_HasExpectedEnumValues()
    {
        var values = Enum.GetValues(typeof(ThemeMode)).Cast<ThemeMode>().ToList();
        values.Should().Contain(new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System, ThemeMode.HighContrast });
        values.Should().HaveCount(4);
    }

    [Fact]
    public void Theme_IconOverrides_DefaultsToNull()
    {
        var theme = new Theme("test", ThemeMode.Dark, BuiltInThemes.Dark.Colors, BuiltInThemes.DefaultTypography, BuiltInThemes.DefaultMetrics);

        theme.IconOverrides.Should().BeNull();
    }
}
