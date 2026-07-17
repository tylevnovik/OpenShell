using FluentAssertions;
using OpenShell.Items;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Items;

/// <summary>
/// Item 单元测试。Per ADR-0003, ADR-0033.
/// </summary>
public class ItemTests
{
    [Fact]
    public void File_Factory_SetsKindToFile()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var item = Item.File(path);
        item.Kind.Should().Be(ItemKind.File);
    }

    [Fact]
    public void File_Factory_SetsSizeWhenProvided()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var item = Item.File(path, size: 1024);
        item.Size.Should().Be(1024);
    }

    [Fact]
    public void File_Factory_SizeDefaultsNull()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var item = Item.File(path);
        item.Size.Should().BeNull();
    }

    [Fact]
    public void File_Factory_SetsModifiedWhenProvided()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var modified = DateTimeOffset.UtcNow;
        var item = Item.File(path, modified: modified);
        item.Timestamps.Modified.Should().Be(modified);
    }

    [Fact]
    public void Directory_Factory_SetsKindToDirectory()
    {
        var path = ItemPath.Parse("fs::/tmp/dir");
        var item = Item.Directory(path);
        item.Kind.Should().Be(ItemKind.Directory);
    }

    [Fact]
    public void Directory_Factory_SizeIsNull()
    {
        var path = ItemPath.Parse("fs::/tmp/dir");
        var item = Item.Directory(path);
        item.Size.Should().BeNull();
    }

    [Fact]
    public void Name_DerivedFromPath()
    {
        var path = ItemPath.Parse("fs::/tmp/foo/bar.txt");
        var item = Item.File(path);
        item.Name.Should().Be("bar.txt");
    }

    [Fact]
    public void Equality_SamePathKind_AreEqual()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var a = Item.File(path, size: 100);
        var b = Item.File(path, size: 100);
        a.Should().Be(b);
    }

    [Fact]
    public void Equality_DifferentSize_NotEqual()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var a = Item.File(path, size: 100);
        var b = Item.File(path, size: 200);
        a.Should().NotBe(b);
    }

    [Fact]
    public void Properties_DefaultEmpty()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var item = Item.File(path);
        item.Properties.Values.Should().BeEmpty();
    }

    [Fact]
    public void ContentType_DefaultNull()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var item = Item.File(path);
        item.ContentType.Should().BeNull();
    }

    [Fact]
    public void Timestamps_DefaultNone()
    {
        var path = ItemPath.Parse("fs::/tmp/dir");
        var item = Item.Directory(path);
        item.Timestamps.Created.Should().BeNull();
        item.Timestamps.Modified.Should().BeNull();
        item.Timestamps.Accessed.Should().BeNull();
    }
}
