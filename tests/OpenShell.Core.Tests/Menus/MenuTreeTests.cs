using FluentAssertions;
using OpenShell.Menus;
using Xunit;

namespace OpenShell.Core.Tests.Menus;

/// <summary>
/// Unit tests for <see cref="MenuTree"/>. Per ADR-0028 section 3.
/// </summary>
public class MenuTreeTests
{
    private static MenuItemContribution Contribution(string path, int order = 0, string? label = null) =>
        new()
        {
            Path = path,
            CommandId = "test-command",
            Label = label,
            Order = order,
        };

    [Fact]
    public void Add_SingleContribution_BuildsLeafPath()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("context/copy"));

        var contextGroup = tree.Root.FindChild("context");
        contextGroup.Should().NotBeNull();
        contextGroup!.FindChild("copy").Should().NotBeNull();

        var copyNode = contextGroup.FindChild("copy");
        copyNode!.Contribution.Should().NotBeNull();
        copyNode.Contribution!.Path.Should().Be("context/copy");
    }

    [Fact]
    public void Add_NestedContributions_ShareIntermediateNodes()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("context/new/folder"));
        tree.Add(Contribution("context/new/file"));

        var contextNode = tree.Root.FindChild("context");
        contextNode.Should().NotBeNull();

        var newNode = contextNode!.FindChild("new");
        newNode.Should().NotBeNull();
        newNode!.Children.Should().HaveCount(2);
        newNode.FindChild("folder").Should().NotBeNull();
        newNode.FindChild("file").Should().NotBeNull();
    }

    [Fact]
    public void Add_SamePathTwice_OverwritesContribution()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("context/copy", label: "First"));
        tree.Add(Contribution("context/copy", label: "Second"));

        var copyNode = tree.Root.FindChild("context")!.FindChild("copy");
        copyNode!.Contribution!.Label.Should().Be("Second");
    }

    [Fact]
    public void Add_TopLevelGroups_SeparateNodes()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("context/copy"));
        tree.Add(Contribution("toolbar/copy"));

        tree.Root.Children.Should().HaveCount(2);
        tree.Root.FindChild("context").Should().NotBeNull();
        tree.Root.FindChild("toolbar").Should().NotBeNull();
    }

    [Fact]
    public void Add_SeparatorContribution_MarksNode()
    {
        var tree = new MenuTree();
        var sep = new MenuItemContribution
        {
            Path = "context/sep",
            CommandId = "",
            IsSeparator = true,
        };
        tree.Add(sep);

        var sepNode = tree.Root.FindChild("context")!.FindChild("sep");
        sepNode!.IsSeparator.Should().BeTrue();
    }

    [Fact]
    public void Add_SetsOrderOnLeafNode()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("context/copy", order: 42));

        var copyNode = tree.Root.FindChild("context")!.FindChild("copy");
        copyNode!.Order.Should().Be(42);
    }

    [Fact]
    public void GetGroup_ReturnsChildrenOfTopLevelGroup()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("context/copy", order: 1));
        tree.Add(Contribution("context/paste", order: 2));
        tree.Add(Contribution("toolbar/refresh", order: 1));

        var contextChildren = tree.GetGroup("context");
        contextChildren.Should().HaveCount(2);
        contextChildren.Select(n => n.Id).Should().Contain(new[] { "copy", "paste" });
    }

    [Fact]
    public void GetGroup_MissingGroup_ReturnsEmptyList()
    {
        var tree = new MenuTree();
        var children = tree.GetGroup("nonexistent");
        children.Should().BeEmpty();
    }

    [Fact]
    public void Add_MultiSegmentPath_AllSegmentsCreated()
    {
        var tree = new MenuTree();
        tree.Add(Contribution("menubar/file/new/file"));

        var menubar = tree.Root.FindChild("menubar");
        menubar.Should().NotBeNull();
        var file = menubar!.FindChild("file");
        file.Should().NotBeNull();
        var newGroup = file!.FindChild("new");
        newGroup.Should().NotBeNull();
        var fileLeaf = newGroup!.FindChild("file");
        fileLeaf!.Contribution.Should().NotBeNull();
    }

    [Fact]
    public void Add_PathWithEmptySegments_SkipsEmpty()
    {
        var tree = new MenuTree();
        // Double slash produces an empty segment; tree should not crash.
        tree.Add(Contribution("context//copy"));

        var contextNode = tree.Root.FindChild("context");
        contextNode.Should().NotBeNull();
        contextNode!.FindChild("copy").Should().NotBeNull();
    }

    [Fact]
    public void Add_NullContribution_Throws()
    {
        var tree = new MenuTree();
        var act = () => tree.Add(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
