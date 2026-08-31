#nullable enable

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Items;
using OpenShell.Preview;
using OpenShell.Paths;
using OpenShell.Providers;
using SixLabors.ImageSharp;
using System.Reactive.Linq;
using Xunit;

namespace OpenShell.Core.Tests;

/// <summary>
/// 后续可靠性与安全改进的合规测试基线。
/// 尚未实现的目标显式保留 Skip；实现完成后必须移除对应 Skip。
/// </summary>
public sealed class ImprovementHardeningComplianceTests
{
    [Fact]
    public async Task Global_Search_Uses_Index_And_Path_Scope()
    {
        using var temp = new OpenShell.TestUtils.TempDir();
        using var store = new FileIndexStore(temp.GetFullPath("index/files.db"));
        var root = temp.GetFullPath("scope");
        var inScope = Path.Combine(root, "report.txt");
        var outOfScope = temp.GetFullPath("other/report.txt");
        store.Upsert(inScope, "report.txt", 1, 1);
        store.Upsert(outOfScope, "report.txt", 2, 2);
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var location = new ItemPath { Provider = "fs", InternalPath = root.Replace('\\', '/') };
        var ctx = new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new ComplianceHost(location, services),
            CurrentLocation = location,
        };

        var results = new List<IItem>();
        await foreach (var item in new GlobalSearchCommand().ExecuteAsync(
            new GlobalSearchCommand.Args("report", location, false, 10), ctx))
            results.Add(item);

        results.Should().ContainSingle();
        results[0].Path.InternalPath.Should().Be(inScope.Replace('\\', '/'));
    }

    [Fact]
    public void File_Index_Handles_Duplicate_Names_Rename_And_Delete()
    {
        using var temp = new OpenShell.TestUtils.TempDir();
        using var store = new FileIndexStore(temp.GetFullPath("index/files.db"));
        var first = temp.GetFullPath("a/report.txt");
        var second = temp.GetFullPath("b/report.txt");
        store.Upsert(first, "report.txt", 1, 1);
        store.Upsert(second, "report.txt", 2, 2);
        store.SearchByName("report*").Select(x => x.Path).Should().BeEquivalentTo(
            new[] { first.Replace('\\', '/'), second.Replace('\\', '/') });
        store.Upsert(first, "renamed.txt", 3, 3);
        store.SearchByName("report*").Should().ContainSingle(x => x.Path == second.Replace('\\', '/'));
        store.Delete(second);
        store.Delete(first);
        store.HasEntries.Should().BeFalse();
    }

    [Fact]
    public void Protected_Secret_Store_Does_Not_Persist_Plaintext()
    {
        using var temp = new OpenShell.TestUtils.TempDir();
        var path = temp.GetFullPath("secrets.json");
        var store = new OpenShell.Security.ProtectedFileSecretStore(path);

        store.SetSecret("sftp/test/password", "secret-value");

        File.ReadAllText(path).Should().NotContain("secret-value");
        store.GetSecret("sftp/test/password").Should().Be("secret-value");
    }

    [Fact]
    public async Task Production_Trust_Rejects_Invalid_Artifacts()
    {
        using var temp = new OpenShell.TestUtils.TempDir();
        var missing = temp.GetFullPath("missing.bin");
        var verifier = new OpenShell.Updates.PlatformCodeSignatureVerifier();
        (await verifier.VerifyAsync(missing)).Should().BeFalse();

        // Linux 没有统一的平台签名 API, 由 SHA-256/Ed25519 上游校验承担完整性。
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return;

        var unsigned = temp.GetFullPath("unsigned.bin");
        await File.WriteAllTextAsync(unsigned, "not a signed executable");
        (await verifier.VerifyAsync(unsigned)).Should().BeFalse();
    }

    [Fact]
    public async Task Provider_Install_Rolls_Back_On_Activation_Failure()
    {
        await new OpenShell.Core.Tests.Packaging.ProviderInstallerTests()
            .InstallAsync_WhenConfigCommitFails_RestoresPreviousCurrentAndVersion();
    }

    [Fact]
    public async Task Global_Search_Can_Include_File_Contents()
    {
        var provider = new ContentSearchProvider();
        var providers = new ProviderRegistry();
        providers.Register(provider);
        var location = new ItemPath { Provider = "test", InternalPath = "/" };
        var ctx = new CommandContext
        {
            Providers = providers,
            Commands = new CommandRegistry(),
            Host = new ComplianceHost(location, new ServiceCollection().BuildServiceProvider()),
            CurrentLocation = location,
        };

        var results = new List<IItem>();
        await foreach (var item in new GlobalSearchCommand().ExecuteAsync(
            new GlobalSearchCommand.Args("needle", location, true, 10), ctx))
            results.Add(item);

        results.Should().ContainSingle();
        results[0].Name.Should().Be("notes.txt");
    }

    [Fact]
    public void Evaluator_Async_Stream_Does_Not_Block()
    {
        // IH-008: 在"从不泵送延续"的 UI 风格同步上下文里同步消费异步流与 Task,
        // 必须在有限时间内完成——等待被搬到线程池, 延续不再依赖被阻塞的上下文。
        var fakeContext = new NeverRunSynchronizationContext();
        object? streamResult = null;
        object? taskResult = null;
        Exception? error = null;
        var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(fakeContext);
            try
            {
                var evaluator = new OpenShell.Runtime.Evaluator(
                    new OpenShell.Runtime.ExecutionContext(variables: new OpenShell.Variables.InMemoryVariableRegistry()));
                streamResult = evaluator.UnwrapAwaitable(DelayedItemStream());
                taskResult = evaluator.UnwrapAwaitable(Task.Run(async () =>
                {
                    await Task.Delay(30);
                    return 42;
                }));
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                finished.Set();
            }
        })
        { IsBackground = true };
        thread.Start();

        finished.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "同步消费异步流不得与未泵送的同步上下文形成死锁");
        error.Should().BeNull();
        fakeContext.PostCount.Should().Be(0, "延续不应排队回被阻塞的上下文");
        streamResult.Should().BeAssignableTo<System.Collections.IEnumerable>();
        ((System.Collections.IEnumerable)streamResult!).Cast<object>().Should().ContainSingle();
        taskResult.Should().Be(42);
    }

    private static async IAsyncEnumerable<IItem> DelayedItemStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(50, ct);
        yield return Item.File(new ItemPath { Provider = "fs", InternalPath = "/a.txt" }, size: 1);
    }

    private sealed class NeverRunSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
            => Interlocked.Increment(ref _postCount); // 故意不执行, 模拟未泵送的 UI 消息队列

        public override void Send(SendOrPostCallback d, object? state)
            => Interlocked.Increment(ref _postCount);
    }

    [Fact]
    public async Task Preview_Formats_Render_Or_Downgrade_Explicitly()
    {
        using var temp = new OpenShell.TestUtils.TempDir();
        var previewer = new ImagePreviewer(
            (path, ct) => Task.FromResult<Stream>(File.OpenRead(ToLocalPath(path))));

        // IH-009: 常见光栅格式必须能渲染为 Image (统一转 PNG)。
        foreach (var (ext, encoder) in new (string, SixLabors.ImageSharp.Formats.IImageEncoder)[]
        {
            (".png", new SixLabors.ImageSharp.Formats.Png.PngEncoder()),
            (".jpg", new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder()),
            (".bmp", new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder()),
            (".gif", new SixLabors.ImageSharp.Formats.Gif.GifEncoder()),
            (".webp", new SixLabors.ImageSharp.Formats.Webp.WebpEncoder()),
        })
        {
            var file = temp.GetFullPath($"sample{ext}");
            using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(48, 32))
                img.Save(file, encoder);

            var preview = await previewer.CreatePreviewAsync(
                FileItem(file), PreviewOptions(), CancellationToken.None);

            preview.Should().BeOfType<PreviewViewModel.Image>($"'{ext}' 必须可解码, 实际: {Describe(preview)}");
            var image = (PreviewViewModel.Image)preview!;
            image.Width.Should().Be(48);
            image.Height.Should().Be(32);
            image.PngData.Length.Should().BeGreaterThan(0);
        }

        // 损坏的光栅文件: 明确降级为 NotSupported, 不抛异常。
        var corrupt = temp.GetFullPath("corrupt.png");
        await File.WriteAllBytesAsync(corrupt, [0x00, 0x01, 0x02, 0x03]);
        (await previewer.CreatePreviewAsync(FileItem(corrupt), PreviewOptions(), CancellationToken.None))
            .Should().BeOfType<PreviewViewModel.NotSupported>();

        // SVG: 无矢量渲染能力, 明确 NotSupported。
        var svg = temp.GetFullPath("vector.svg");
        await File.WriteAllTextAsync(svg, "<svg xmlns='http://www.w3.org/2000/svg'/>");
        (await previewer.CreatePreviewAsync(FileItem(svg), PreviewOptions(), CancellationToken.None))
            .Should().BeOfType<PreviewViewModel.NotSupported>();

        // 资源上限: 超过输入字节上限时拒绝解码并明确说明。
        var smallLimitPreviewer = new ImagePreviewer(
            (path, ct) => Task.FromResult<Stream>(File.OpenRead(ToLocalPath(path))), maxInputBytes: 16);
        var tooLarge = temp.GetFullPath("large.png");
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64))
            img.Save(tooLarge, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        (await smallLimitPreviewer.CreatePreviewAsync(FileItem(tooLarge), PreviewOptions(), CancellationToken.None))
            .Should().BeOfType<PreviewViewModel.NotSupported>();
    }

    private static IItem FileItem(string fullPath)
        => Item.File(new ItemPath { Provider = "fs", InternalPath = fullPath.Replace('\\', '/') });

    private static PreviewOptions PreviewOptions() => new();

    private static string ToLocalPath(ItemPath path)
        => path.InternalPath.Replace('/', Path.DirectorySeparatorChar);

    private static string Describe(PreviewViewModel? preview) => preview switch
    {
        PreviewViewModel.NotSupported ns => $"NotSupported({ns.Reason})",
        null => "null",
        _ => preview.GetType().Name,
    };

    private sealed class ComplianceHost : OpenShell.IHost
    {
        public ComplianceHost(ItemPath location, IServiceProvider services)
        {
            CurrentLocation = location;
            Services = services;
        }

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; }
        public IObservable<IReadOnlyList<IItem>> Selection => Observable.Empty<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress { get; } = new Progress<OperationProgress>();
        public IServiceProvider Services { get; }
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ContentSearchProvider : IProvider, IContainerProvider, IContentProvider
    {
        private readonly Item _item = Item.File(
            new ItemPath { Provider = "test", InternalPath = "/notes.txt" }, size: 12);

        public ProviderInfo Info { get; } = new()
        {
            Name = "test",
            Version = new Version(1, 0, 0),
        };
        public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
        {
            ProviderCapability.Container,
            ProviderCapability.Content,
        };

        public async IAsyncEnumerable<IItem> GetChildrenAsync(
            ItemPath path,
            OpenShell.Paths.EnumerationOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return _item;
        }

        public ValueTask<Stream> OpenReadAsync(
            ItemPath path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(
                new MemoryStream("this line contains needle"u8.ToArray(), writable: false));
    }
}
