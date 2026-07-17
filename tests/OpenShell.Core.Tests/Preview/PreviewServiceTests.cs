using FluentAssertions;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Preview;
using Xunit;

namespace OpenShell.Core.Tests.Preview;

/// <summary>
/// ADR-0030 §1: PreviewService 协调器单测。
/// 验证: 多 previewer 链式选择, 第一个不支持的由下一个处理; 都不支持返回 NotSupported。
/// </summary>
public class PreviewServiceTests
{
    /// <summary>可编程的假 previewer, 用于验证协调逻辑。</summary>
    private sealed class FakePreviewer : IPreviewer
    {
        private readonly Func<IItem, bool> _canPreview;
        private readonly Func<IItem, PreviewViewModel?> _create;

        public FakePreviewer(Func<IItem, bool> canPreview, Func<IItem, PreviewViewModel?> create)
        {
            _canPreview = canPreview;
            _create = create;
        }

        public bool CanPreview(IItem item) => _canPreview(item);

        public ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
            => new(_create(item));
    }

    private static IItem MakeFile(string name) =>
        Item.File(ItemPath.Parse($"fs::C:/tmp/{name}"));

    [Fact]
    public void CanPreview_AnyPreviewerSupports_ReturnsTrue()
    {
        var svc = new PreviewService(new IPreviewer[]
        {
            new FakePreviewer(_ => false, _ => null),
            new FakePreviewer(_ => true, _ => new PreviewViewModel.NotSupported("never")),
        });

        svc.CanPreview(MakeFile("a.txt")).Should().BeTrue();
    }

    [Fact]
    public void CanPreview_NoPreviewerSupports_ReturnsFalse()
    {
        var svc = new PreviewService(new IPreviewer[]
        {
            new FakePreviewer(_ => false, _ => null),
            new FakePreviewer(_ => false, _ => null),
        });

        svc.CanPreview(MakeFile("a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task CreatePreviewAsync_FirstSupportedPreviewerHandles()
    {
        // 第一个 previewer 不支持, 第二个支持并返回 Text。
        var svc = new PreviewService(new IPreviewer[]
        {
            new FakePreviewer(_ => false, _ => null),
            new FakePreviewer(_ => true, _ => new PreviewViewModel.Text("content", null, 1, false)),
        });

        var vm = await svc.CreatePreviewAsync(MakeFile("a.txt"), new PreviewOptions(), default);

        vm.Should().NotBeNull();
        var text = vm.Should().BeOfType<PreviewViewModel.Text>().Subject;
        text.Content.Should().Be("content");
    }

    [Fact]
    public async Task CreatePreviewAsync_NoneSupported_ReturnsNotSupported()
    {
        var svc = new PreviewService(new IPreviewer[]
        {
            new FakePreviewer(_ => false, _ => null),
            new FakePreviewer(_ => false, _ => null),
        });

        var vm = await svc.CreatePreviewAsync(MakeFile("a.zip"), new PreviewOptions(), default);

        vm.Should().NotBeNull();
        vm.Should().BeOfType<PreviewViewModel.NotSupported>()
            .Which.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreatePreviewAsync_EmptyPreviewerList_ReturnsNotSupported()
    {
        var svc = new PreviewService(Array.Empty<IPreviewer>());

        var vm = await svc.CreatePreviewAsync(MakeFile("a.txt"), new PreviewOptions(), default);

        vm.Should().NotBeNull();
        vm.Should().BeOfType<PreviewViewModel.NotSupported>();
    }

    [Fact]
    public async Task CreatePreviewAsync_FirstPreviewerWins_WhenMultipleSupport()
    {
        // 两个 previewer 都支持, 第一个返回 Text, 应优先返回第一个。
        var svc = new PreviewService(new IPreviewer[]
        {
            new FakePreviewer(_ => true, _ => new PreviewViewModel.Text("first", null, 1, false)),
            new FakePreviewer(_ => true, _ => new PreviewViewModel.Text("second", null, 1, false)),
        });

        var vm = await svc.CreatePreviewAsync(MakeFile("a.txt"), new PreviewOptions(), default);

        var text = vm.Should().BeOfType<PreviewViewModel.Text>().Subject;
        text.Content.Should().Be("first");
    }
}
