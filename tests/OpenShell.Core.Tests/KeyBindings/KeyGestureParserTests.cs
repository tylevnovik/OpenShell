using FluentAssertions;
using OpenShell.KeyBindings;
using Xunit;

namespace OpenShell.Core.Tests.KeyBindings;

/// <summary>
/// Tests for KeyGestureParser. Per ADR-0027 section 2.
/// </summary>
public class KeyGestureParserTests
{
    // ---- Basic parsing ---------------------------------------------------

    [Fact]
    public void Parse_CtrlShiftP()
    {
        var g = KeyGestureParser.Parse("Ctrl+Shift+P");
        g.Modifiers.Should().Be(KeyModifiers.Control | KeyModifiers.Shift);
        g.Key.Should().Be("P");
    }

    [Fact]
    public void Parse_F5_NoModifiers()
    {
        var g = KeyGestureParser.Parse("F5");
        g.Modifiers.Should().Be(KeyModifiers.None);
        g.Key.Should().Be("F5");
    }

    [Fact]
    public void Parse_AltEnter()
    {
        var g = KeyGestureParser.Parse("Alt+Enter");
        g.Modifiers.Should().Be(KeyModifiers.Alt);
        g.Key.Should().Be("Enter");
    }

    [Fact]
    public void Parse_CmdC_Meta()
    {
        var g = KeyGestureParser.Parse("Cmd+C");
        g.Modifiers.Should().Be(KeyModifiers.Meta);
        g.Key.Should().Be("C");
    }

    [Fact]
    public void Parse_ControlAlias()
    {
        var g = KeyGestureParser.Parse("Control+A");
        g.Modifiers.Should().Be(KeyModifiers.Control);
        g.Key.Should().Be("A");
    }

    [Fact]
    public void Parse_OptionAlias_Alt()
    {
        var g = KeyGestureParser.Parse("Option+X");
        g.Modifiers.Should().Be(KeyModifiers.Alt);
        g.Key.Should().Be("X");
    }

    [Fact]
    public void Parse_WinAlias_Meta()
    {
        var g = KeyGestureParser.Parse("Win+D");
        g.Modifiers.Should().Be(KeyModifiers.Meta);
        g.Key.Should().Be("D");
    }

    [Fact]
    public void Parse_MetaAlias_Meta()
    {
        var g = KeyGestureParser.Parse("Meta+L");
        g.Modifiers.Should().Be(KeyModifiers.Meta);
        g.Key.Should().Be("L");
    }

    // ---- Normalization ---------------------------------------------------

    [Fact]
    public void Normalize_LowercaseKey_FirstUpper()
    {
        KeyGesture.NormalizeKey("p").Should().Be("P");
        KeyGesture.NormalizeKey("f5").Should().Be("F5");
        KeyGesture.NormalizeKey("enter").Should().Be("Enter");
        KeyGesture.NormalizeKey("backspace").Should().Be("Backspace");
    }

    [Fact]
    public void Parse_NormalizesKeyToken()
    {
        KeyGestureParser.Parse("ctrl+shift+p").Key.Should().Be("P");
        KeyGestureParser.Parse("f5").Key.Should().Be("F5");
        KeyGestureParser.Parse("Alt+enter").Key.Should().Be("Enter");
    }

    [Fact]
    public void Parse_IsCaseInsensitiveForModifiers()
    {
        var g = KeyGestureParser.Parse("ctrl+SHIFT+p");
        g.Modifiers.Should().Be(KeyModifiers.Control | KeyModifiers.Shift);
        g.Key.Should().Be("P");
    }

    // ---- Format ----------------------------------------------------------

    [Fact]
    public void Format_CtrlShiftP()
    {
        var g = new KeyGesture(KeyModifiers.Control | KeyModifiers.Shift, "P");
        KeyGestureParser.Format(g).Should().Be("Ctrl+Shift+P");
    }

    [Fact]
    public void Format_F5()
    {
        var g = new KeyGesture(KeyModifiers.None, "F5");
        KeyGestureParser.Format(g).Should().Be("F5");
    }

    [Fact]
    public void Format_AltEnter()
    {
        var g = new KeyGesture(KeyModifiers.Alt, "Enter");
        KeyGestureParser.Format(g).Should().Be("Alt+Enter");
    }

    [Fact]
    public void Format_CmdC()
    {
        var g = new KeyGesture(KeyModifiers.Meta, "C");
        KeyGestureParser.Format(g).Should().Be("Cmd+C");
    }

    [Fact]
    public void DisplayString_MatchesFormat()
    {
        var g = new KeyGesture(KeyModifiers.Control | KeyModifiers.Shift, "P");
        g.DisplayString.Should().Be(KeyGestureParser.Format(g));
        g.DisplayString.Should().Be("Ctrl+Shift+P");
    }

    // ---- Round-trip ------------------------------------------------------

    [Theory]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("F5")]
    [InlineData("Alt+Enter")]
    [InlineData("Cmd+C")]
    [InlineData("Control+A")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Shift+Tab")]
    [InlineData("Backspace")]
    [InlineData("Ctrl+Alt+Shift+Cmd+X")]
    public void FormatRoundTrip_ParsesBack(string gestureText)
    {
        var g = KeyGestureParser.Parse(gestureText);
        KeyGestureParser.Parse(KeyGestureParser.Format(g)).Should().Be(g);
    }

    // ---- Invalid input ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_Throws(string text)
    {
        var act = () => KeyGestureParser.Parse(text);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_TrailingPlus_Throws()
    {
        var act = () => KeyGestureParser.Parse("Ctrl+");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ModifierOnlyNoKey_Throws()
    {
        var act = () => KeyGestureParser.Parse("Ctrl");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_TwoModifiersNoKey_Throws()
    {
        var act = () => KeyGestureParser.Parse("Shift+Ctrl");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_UnknownModifier_Throws()
    {
        var act = () => KeyGestureParser.Parse("Foo+P");
        act.Should().Throw<ArgumentException>();
    }
}
