using FluentAssertions;
using OpenShell.Menus;
using Xunit;

namespace OpenShell.Core.Tests.Menus;

/// <summary>
/// Unit tests for <see cref="MenuContext"/> and <see cref="SelectionInfo"/>.
/// Per ADR-0028 section 2.
/// </summary>
public class MenuContextTests
{
    [Fact]
    public void ToDictionary_Defaults_IncludesAllExpectedKeys()
    {
        var ctx = new MenuContext();
        var dict = ctx.ToDictionary();

        dict.Should().ContainKey("focus");
        dict.Should().ContainKey("selected.count");
        dict.Should().ContainKey("selected.allDirectories");
        dict.Should().ContainKey("selected.allFiles");
        dict.Should().ContainKey("selected.containsArchive");
        dict.Should().ContainKey("selected.singleItem");
        dict.Should().ContainKey("provider");
    }

    [Fact]
    public void ToDictionary_Defaults_CountZero_AllBooleansFalse()
    {
        var ctx = new MenuContext();
        var dict = ctx.ToDictionary();

        dict["focus"].Should().Be("");
        dict["selected.count"].Should().Be(0);
        dict["selected.allDirectories"].Should().Be(false);
        dict["selected.allFiles"].Should().Be(false);
        dict["selected.containsArchive"].Should().Be(false);
        dict["selected.singleItem"].Should().Be(false);
        dict["provider"].Should().Be("");
    }

    [Fact]
    public void ToDictionary_WithSelection_ReflectsValues()
    {
        var ctx = new MenuContext
        {
            FocusedElement = "pane",
            Selection = new SelectionInfo
            {
                Count = 3,
                AllDirectories = true,
                AllFiles = false,
                ContainsArchive = true,
            },
            CurrentProvider = "fs",
        };
        var dict = ctx.ToDictionary();

        dict["focus"].Should().Be("pane");
        dict["selected.count"].Should().Be(3);
        dict["selected.allDirectories"].Should().Be(true);
        dict["selected.allFiles"].Should().Be(false);
        dict["selected.containsArchive"].Should().Be(true);
        dict["selected.singleItem"].Should().Be(false);
        dict["provider"].Should().Be("fs");
    }

    [Fact]
    public void SelectionInfo_SingleItem_TrueWhenCountOne()
    {
        var info = new SelectionInfo { Count = 1 };
        info.SingleItem.Should().BeTrue();
    }

    [Fact]
    public void SelectionInfo_SingleItem_FalseWhenCountNotOne()
    {
        new SelectionInfo { Count = 0 }.SingleItem.Should().BeFalse();
        new SelectionInfo { Count = 2 }.SingleItem.Should().BeFalse();
        new SelectionInfo { Count = 10 }.SingleItem.Should().BeFalse();
    }

    [Fact]
    public void MenuContext_DefaultSelection_IsNull_YieldsZeroCount()
    {
        var ctx = new MenuContext();
        ctx.Selection.Should().BeNull();
        var dict = ctx.ToDictionary();
        dict["selected.count"].Should().Be(0);
        dict["selected.singleItem"].Should().Be(false);
    }

    [Fact]
    public void ToDictionary_ReturnsReadOnlyDictionary()
    {
        var ctx = new MenuContext();
        var dict = ctx.ToDictionary();
        dict.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
    }

    [Fact]
    public void MenuContext_RecordEquality_WorksAsExpected()
    {
        var a = new MenuContext("pane", new SelectionInfo { Count = 1 }, "fs::C:/", "fs");
        var b = new MenuContext("pane", new SelectionInfo { Count = 1 }, "fs::C:/", "fs");
        a.Should().Be(b);
    }
}
