using FluentAssertions;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Paths;

/// <summary>
/// ItemPath 单元测试。Per ADR-0006, ADR-0033.
/// 命名约定：Method_Scenario_Expected。
/// </summary>
public class ItemPathTests
{
    [Fact]
    public void Parse_WithProviderPrefix_ReturnsProviderAndInternalPath()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo");
        path.Provider.Should().Be("fs");
        path.InternalPath.Should().Be("C:/Users/foo");
    }

    [Fact]
    public void Parse_BarePath_AssumesFsProvider()
    {
        var path = ItemPath.Parse("sub/dir/file.txt");
        path.Provider.Should().Be("fs");
        path.InternalPath.Should().Be("sub/dir/file.txt");
    }

    [Fact]
    public void Parse_Backslash_NormalisedToSlash()
    {
        var path = ItemPath.Parse("fs::C:\\Users\\foo");
        path.InternalPath.Should().Be("C:/Users/foo");
    }

    [Fact]
    public void Parse_Empty_Throws()
    {
        Action act = () => ItemPath.Parse("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_Whitespace_Throws()
    {
        Action act = () => ItemPath.Parse("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ProviderLowercased()
    {
        var path = ItemPath.Parse("FS::foo");
        path.Provider.Should().Be("fs");
    }

    [Fact]
    public void Display_ConcatenatesProviderAndInternal()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo");
        path.Display.Should().Be("fs::C:/Users/foo");
    }

    [Fact]
    public void Combine_RelativePath_AppendsWithSeparator()
    {
        var path = ItemPath.Parse("fs::C:/Users");
        var combined = path.Combine("foo/bar.txt");
        combined.InternalPath.Should().Be("C:/Users/foo/bar.txt");
        combined.Provider.Should().Be("fs");
    }

    [Fact]
    public void Combine_AbsolutePath_ReplacesInternal()
    {
        var path = ItemPath.Parse("fs::C:/Users");
        var combined = path.Combine("/etc/passwd");
        combined.InternalPath.Should().Be("/etc/passwd");
    }

    [Fact]
    public void Combine_Empty_ReturnsSamePath()
    {
        var path = ItemPath.Parse("fs::C:/Users");
        var combined = path.Combine("");
        combined.Should().Be(path);
    }

    [Fact]
    public void GetParent_DeepPath_ReturnsParent()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo/bar.txt");
        var parent = path.GetParent();
        parent.InternalPath.Should().Be("C:/Users/foo");
    }

    [Fact]
    public void GetParent_SingleSegment_ReturnsRoot()
    {
        var path = ItemPath.Parse("fs::foo");
        var parent = path.GetParent();
        parent.InternalPath.Should().Be("foo/");
    }

    [Fact]
    public void GetName_DeepPath_ReturnsLastSegment()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo/bar.txt");
        path.GetName().Should().Be("bar.txt");
    }

    [Fact]
    public void GetName_SingleSegment_ReturnsItself()
    {
        var path = ItemPath.Parse("fs::bar.txt");
        path.GetName().Should().Be("bar.txt");
    }

    [Fact]
    public void IsRooted_AbsolutePath_ReturnsTrue()
    {
        var path = ItemPath.Parse("fs::/etc/passwd");
        path.IsRooted.Should().BeTrue();
    }

    [Fact]
    public void IsRooted_WindowsDrive_ReturnsTrue()
    {
        var path = ItemPath.Parse("fs::C:/Users");
        path.IsRooted.Should().BeTrue();
    }

    [Fact]
    public void IsRooted_RelativePath_ReturnsFalse()
    {
        var path = ItemPath.Parse("fs::foo/bar");
        path.IsRooted.Should().BeFalse();
    }

    [Fact]
    public void Equality_SamePaths_AreEqual()
    {
        var a = ItemPath.Parse("fs::C:/Users/foo");
        var b = ItemPath.Parse("fs::C:/Users/foo");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentProviders_NotEqual()
    {
        var a = ItemPath.Parse("fs::C:/Users");
        var b = ItemPath.Parse("zip::C:/Users");
        a.Should().NotBe(b);
    }

    [Fact]
    public void Root_StaticFactory_ReturnsRootedPath()
    {
        var root = ItemPath.Root("fs");
        root.Provider.Should().Be("fs");
        root.InternalPath.Should().Be("/");
        root.IsRooted.Should().BeTrue();
    }

    [Fact]
    public void FriendlyName_ReturnsInternalPath()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo");
        path.FriendlyName.Should().Be("C:/Users/foo");
    }

    [Fact]
    public void ToString_EqualsDisplay()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo");
        path.ToString().Should().Be(path.Display);
    }
}
