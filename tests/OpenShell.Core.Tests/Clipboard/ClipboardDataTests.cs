using System.Text.Json;
using FluentAssertions;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Clipboard;
using Xunit;

namespace OpenShell.Core.Tests.Clipboard;

/// <summary>
/// ADR-0029 §2 / §3: ClipboardData 静态工具类单测。
/// 验证 OpenShellItems JSON / text/uri-list / text/plain 格式之间的转换。
/// </summary>
public class ClipboardDataTests
{
    [Fact]
    public void SerializeItems_AndDeserializeItems_RoundTrip_PreservesItemsAndWasCut()
    {
        var items = new List<IItem>
        {
            Item.File(ItemPath.Parse("fs::C:/Users/me/file.txt"), size: 100),
            Item.Directory(ItemPath.Parse("fs::C:/Users/me/sub")),
        };

        var json = ClipboardData.SerializeItems(items, cut: false);
        var (roundTripped, wasCut) = ClipboardData.DeserializeItems(json);

        roundTripped.Should().HaveCount(items.Count);
        roundTripped[0].Path.Display.Should().Be("fs::C:/Users/me/file.txt");
        roundTripped[0].Kind.Should().Be(ItemKind.File);
        roundTripped[0].Size.Should().Be(100);
        roundTripped[1].Path.Display.Should().Be("fs::C:/Users/me/sub");
        roundTripped[1].Kind.Should().Be(ItemKind.Directory);
        wasCut.Should().BeFalse();
    }

    [Fact]
    public void SerializeItems_WithCutTrue_PreservesWasCutFlag()
    {
        var items = new List<IItem>
        {
            Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")),
        };

        var json = ClipboardData.SerializeItems(items, cut: true);
        var (roundTripped, wasCut) = ClipboardData.DeserializeItems(json);

        roundTripped.Should().HaveCount(1);
        wasCut.Should().BeTrue();
    }

    [Fact]
    public void SerializeItems_IncludesWasCutAndTimestampInJson()
    {
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        var json = ClipboardData.SerializeItems(items, cut: true);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("wasCut").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.String);
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void ToUriList_OnlyIncludesFsProviderPaths_MultilineFormat()
    {
        var items = new List<IItem>
        {
            Item.File(ItemPath.Parse("fs::C:/Users/me/file.txt")),
            Item.File(ItemPath.Parse("s3::bucket/key")),
            Item.File(ItemPath.Parse("fs::C:/Users/me/another.png")),
            Item.File(ItemPath.Parse("reg::HKLM/Software")),
        };

        var uriList = ClipboardData.ToUriList(items);

        var lines = uriList.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        lines.Should().HaveCount(2);
        lines[0].Should().Be("C:/Users/me/file.txt");
        lines[1].Should().Be("C:/Users/me/another.png");
        // 不含 provider 前缀, 仅本地路径。
        uriList.Should().NotContain("s3::");
        uriList.Should().NotContain("reg::");
    }

    [Fact]
    public void ToPlainText_IncludesAllProviders_UsesDisplayFormat()
    {
        var items = new List<IItem>
        {
            Item.File(ItemPath.Parse("fs::C:/Users/me/file.txt")),
            Item.File(ItemPath.Parse("s3::bucket/key")),
            Item.File(ItemPath.Parse("reg::HKLM/Software")),
        };

        var plain = ClipboardData.ToPlainText(items);

        var lines = plain.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        lines.Should().HaveCount(3);
        lines[0].Should().Be("fs::C:/Users/me/file.txt");
        lines[1].Should().Be("s3::bucket/key");
        lines[2].Should().Be("reg::HKLM/Software");
    }

    [Fact]
    public void TryParseUriList_ParsesMultilinePaths_AsFsProvider()
    {
        var text = "C:/Users/me/file.txt" + Environment.NewLine + "C:/Users/me/another.png";

        var paths = ClipboardData.TryParseUriList(text);

        paths.Should().HaveCount(2);
        paths[0].Provider.Should().Be("fs");
        paths[0].InternalPath.Should().Be("C:/Users/me/file.txt");
        paths[1].Provider.Should().Be("fs");
        paths[1].InternalPath.Should().Be("C:/Users/me/another.png");
    }

    [Fact]
    public void TryParseUriList_SupportsFileSchemeUris()
    {
        var text = "file:///C:/Users/me/file.txt" + Environment.NewLine + "file:///home/user/doc";

        var paths = ClipboardData.TryParseUriList(text);

        paths.Should().HaveCount(2);
        paths[0].Provider.Should().Be("fs");
        paths[0].InternalPath.Should().Be("C:/Users/me/file.txt");
        paths[1].Provider.Should().Be("fs");
        paths[1].InternalPath.Should().Be("/home/user/doc");
    }

    [Fact]
    public void TryParseUriList_SkipsBlankLinesAndComments()
    {
        // RFC 2483: # 开头为注释, 空行被忽略。
        var text = "# comment line" + Environment.NewLine
            + "C:/path/one" + Environment.NewLine
            + Environment.NewLine
            + "C:/path/two" + Environment.NewLine;

        var paths = ClipboardData.TryParseUriList(text);

        paths.Should().HaveCount(2);
        paths[0].InternalPath.Should().Be("C:/path/one");
        paths[1].InternalPath.Should().Be("C:/path/two");
    }

    [Fact]
    public void TryParseUriList_NormalizesBackslashes()
    {
        var text = "C:\\Users\\me\\file.txt";

        var paths = ClipboardData.TryParseUriList(text);

        paths.Should().HaveCount(1);
        paths[0].InternalPath.Should().Be("C:/Users/me/file.txt");
    }

    [Fact]
    public void SerializeItems_EmptyList_ProducesValidJson()
    {
        var json = ClipboardData.SerializeItems(Array.Empty<IItem>(), cut: false);

        var (items, wasCut) = ClipboardData.DeserializeItems(json);
        items.Should().BeEmpty();
        wasCut.Should().BeFalse();
    }

    [Fact]
    public void ToUriList_EmptyList_ReturnsEmptyString()
    {
        var uriList = ClipboardData.ToUriList(Array.Empty<IItem>());
        uriList.Should().BeEmpty();
    }

    [Fact]
    public void ToPlainText_EmptyList_ReturnsEmptyString()
    {
        var plain = ClipboardData.ToPlainText(Array.Empty<IItem>());
        plain.Should().BeEmpty();
    }

    [Fact]
    public void ToUriList_NoFsPaths_ReturnsEmptyString()
    {
        var items = new List<IItem>
        {
            Item.File(ItemPath.Parse("s3::bucket/key")),
            Item.File(ItemPath.Parse("reg::HKLM/Software")),
        };

        var uriList = ClipboardData.ToUriList(items);
        uriList.Should().BeEmpty();
    }

    [Fact]
    public void TryParseUriList_EmptyText_ReturnsEmptyList()
    {
        var paths = ClipboardData.TryParseUriList(string.Empty);
        paths.Should().BeEmpty();
    }

    [Fact]
    public void TryParseUriList_NullText_ReturnsEmptyList()
    {
        var paths = ClipboardData.TryParseUriList(null!);
        paths.Should().BeEmpty();
    }
}
