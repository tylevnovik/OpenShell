using System.Reflection;
using FluentAssertions;
using OpenShell.Menus;
using Xunit;

namespace OpenShell.Core.Tests.Menus;

/// <summary>
/// Unit tests for <see cref="MenuItemAttribute"/> and <see cref="IconAttribute"/>.
/// Per ADR-0028 section 1.
/// </summary>
public class MenuItemAttributeTests
{
    [Fact]
    public void Constructor_SetsPath()
    {
        var attr = new MenuItemAttribute("context/copy");
        attr.Path.Should().Be("context/copy");
    }

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        var act = () => new MenuItemAttribute(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Defaults_LabelNull_WhenNull_LabelKeyNull_OrderZero()
    {
        var attr = new MenuItemAttribute("context/copy");
        attr.Label.Should().BeNull();
        attr.LabelKey.Should().BeNull();
        attr.When.Should().BeNull();
        attr.Order.Should().Be(0);
        attr.IsSeparator.Should().BeFalse();
        attr.IsDynamic.Should().BeFalse();
    }

    [Fact]
    public void Init_Label_LabelKey_When_Order_Separator_Dynamic()
    {
        var attr = new MenuItemAttribute("context/copy")
        {
            Label = "Copy",
            LabelKey = "menu.copy",
            When = "selected.count > 0",
            Order = 100,
            IsSeparator = false,
            IsDynamic = false,
        };
        attr.Label.Should().Be("Copy");
        attr.LabelKey.Should().Be("menu.copy");
        attr.When.Should().Be("selected.count > 0");
        attr.Order.Should().Be(100);
        attr.IsSeparator.Should().BeFalse();
        attr.IsDynamic.Should().BeFalse();
    }

    [Fact]
    public void Separator_Attribute_FlagStored()
    {
        var attr = new MenuItemAttribute("context/sep1") { IsSeparator = true };
        attr.IsSeparator.Should().BeTrue();
    }

    [Fact]
    public void Dynamic_Attribute_FlagStored()
    {
        var attr = new MenuItemAttribute("context/openWith") { IsDynamic = true };
        attr.IsDynamic.Should().BeTrue();
    }

    [Fact]
    public void AllowMultiple_ClassWithMultipleAttributes_ReturnsAll()
    {
        var attrs = typeof(MultiMenuCommand).GetCustomAttributes<MenuItemAttribute>().ToList();
        attrs.Should().HaveCount(2);
        attrs.Should().Contain(a => a.Path == "context/copy");
        attrs.Should().Contain(a => a.Path == "toolbar/copy");
    }

    [Fact]
    public void IconAttribute_PathStored()
    {
        var attr = new IconAttribute("Icons/copy.svg");
        attr.Path.Should().Be("Icons/copy.svg");
    }

    [Fact]
    public void IconAttribute_NullPath_Throws()
    {
        var act = () => new IconAttribute(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IconAttribute_AllowMultipleFalse_OnlyOne()
    {
        var attrs = typeof(MultiMenuCommand).GetCustomAttributes<IconAttribute>().ToList();
        attrs.Should().HaveCount(1);
        attrs[0].Path.Should().Be("Icons/copy.svg");
    }

    /// <summary>Sample command class used to validate attribute reflection.</summary>
    [OpenShell.Commands.Verb("Copy", Noun = "Item")]
    [MenuItem(Path = "context/copy", When = "selected.count > 0", Order = 100)]
    [MenuItem(Path = "toolbar/copy", When = "selected.count > 0", Order = 100)]
    [Icon("Icons/copy.svg")]
    public sealed class MultiMenuCommand
    {
        public sealed record Args;
    }
}
