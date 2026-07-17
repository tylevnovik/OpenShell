using FluentAssertions;
using OpenShell.Clipboard;
using OpenShell.Items;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Clipboard;

/// <summary>
/// ADR-0029 §1 / §4: InMemoryClipboardService 单测。
/// 验证: items round-trip, wasCut 标记传递, cut 模式粘贴后清除, 文本剪贴板, HasItems 状态。
/// </summary>
public class InMemoryClipboardServiceTests
{
    [Fact]
    public async Task SetItemsAsync_AndGetItemsAsync_RoundTrip()
    {
        var svc = new InMemoryClipboardService();
        var items = new List<IItem>
        {
            Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")),
            Item.Directory(ItemPath.Parse("fs::C:/tmp/sub")),
        };

        await svc.SetItemsAsync(items, cut: false);
        var result = await svc.GetItemsAsync();

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result![0].Path.Display.Should().Be("fs::C:/tmp/a.txt");
        result[1].Path.Display.Should().Be("fs::C:/tmp/sub");
    }

    [Fact]
    public async Task WasCut_DefaultFalse()
    {
        var svc = new InMemoryClipboardService();
        svc.WasCut.Should().BeFalse();
    }

    [Fact]
    public async Task WasCut_TrueAfterSetItemsWithCut()
    {
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        await svc.SetItemsAsync(items, cut: true);

        svc.WasCut.Should().BeTrue();
    }

    [Fact]
    public async Task WasCut_FalseAfterSetItemsWithoutCut()
    {
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        await svc.SetItemsAsync(items, cut: false);

        svc.WasCut.Should().BeFalse();
    }

    [Fact]
    public async Task GetItemsAsync_CopyMode_DoesNotClearClipboard()
    {
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        await svc.SetItemsAsync(items, cut: false);
        var first = await svc.GetItemsAsync();
        var second = await svc.GetItemsAsync();

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second.Should().HaveCount(1);
        // Copy 模式下, 多次 GetItemsAsync 不清除。
        svc.HasItems.Should().BeTrue();
    }

    [Fact]
    public async Task GetItemsAsync_CutMode_ClearsClipboardAfterPaste()
    {
        // ADR-0029 §4 约束: Cut 操作粘贴后必须清除剪贴板。
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        await svc.SetItemsAsync(items, cut: true);
        var first = await svc.GetItemsAsync();
        var second = await svc.GetItemsAsync();

        first.Should().NotBeNull();
        first.Should().HaveCount(1);
        // 粘贴后剪贴板已清空。
        second.Should().BeNull();
        svc.HasItems.Should().BeFalse();
        svc.WasCut.Should().BeFalse();
    }

    [Fact]
    public async Task SetTextAsync_AndGetTextAsync_RoundTrip()
    {
        var svc = new InMemoryClipboardService();

        await svc.SetTextAsync("hello world");
        var text = await svc.GetTextAsync();

        text.Should().Be("hello world");
    }

    [Fact]
    public async Task GetTextAsync_WhenEmpty_ReturnsNull()
    {
        var svc = new InMemoryClipboardService();

        var text = await svc.GetTextAsync();

        text.Should().BeNull();
    }

    [Fact]
    public async Task SetTextAsync_ClearsItemsSlot()
    {
        // SetText 与 SetItems 互斥: 写入纯文本后, items 槽位被清空。
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };
        await svc.SetItemsAsync(items, cut: false);

        await svc.SetTextAsync("plain text");

        svc.HasItems.Should().BeFalse();
        var itemsResult = await svc.GetItemsAsync();
        itemsResult.Should().BeNull();
    }

    [Fact]
    public async Task SetItemsAsync_ClearsTextSlot()
    {
        // SetItems 与 SetText 互斥: 写入 items 后, 纯文本槽位被清空。
        var svc = new InMemoryClipboardService();
        await svc.SetTextAsync("initial text");

        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };
        await svc.SetItemsAsync(items, cut: false);

        var text = await svc.GetTextAsync();
        text.Should().BeNull();
    }

    [Fact]
    public async Task HasItems_FalseByDefault()
    {
        var svc = new InMemoryClipboardService();
        svc.HasItems.Should().BeFalse();
    }

    [Fact]
    public async Task HasItems_TrueAfterSetItems()
    {
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        await svc.SetItemsAsync(items, cut: false);

        svc.HasItems.Should().BeTrue();
    }

    [Fact]
    public async Task HasItems_FalseAfterCutPaste()
    {
        var svc = new InMemoryClipboardService();
        var items = new List<IItem> { Item.File(ItemPath.Parse("fs::C:/tmp/a.txt")) };

        await svc.SetItemsAsync(items, cut: true);
        svc.HasItems.Should().BeTrue();

        await svc.GetItemsAsync();
        svc.HasItems.Should().BeFalse();
    }

    [Fact]
    public async Task HasItems_FalseForEmptyItemsList()
    {
        var svc = new InMemoryClipboardService();

        await svc.SetItemsAsync(Array.Empty<IItem>(), cut: false);

        svc.HasItems.Should().BeFalse();
    }

    [Fact]
    public async Task GetItemsAsync_WhenEmpty_ReturnsNull()
    {
        var svc = new InMemoryClipboardService();

        var items = await svc.GetItemsAsync();

        items.Should().BeNull();
    }
}
