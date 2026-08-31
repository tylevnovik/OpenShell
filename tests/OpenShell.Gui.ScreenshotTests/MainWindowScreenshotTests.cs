using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Gui.Abstractions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Gui.Host.Views;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Providers.FileSystem;
using Xunit;

// IH-011: 本程序集内每个测试自建并销毁 headless+Skia 会话, 必须串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OpenShell.Gui.ScreenshotTests;

/// <summary>
/// IH-011: 桌面视觉验收门禁。
/// 以 Avalonia headless + 真实 Skia 渲染真实 <see cref="MainWindow"/>
/// 于 1200x800 与 800x500 两种验收尺寸, 输出 PNG 到 <c>docs/screenshots/</c> 作为人工复核素材,
/// 同时断言渲染流程无异常、产物非空——每次 CI 都会复跑该门禁。
/// 说明: headless+Skia 是像素级真实布局/绘制, 但不含真实桌面合成器/字体回退环境,
/// 且测试环境未注入 i18n (界面标签显示键名); 三平台真实桌面人工复验仍为待办。
/// 本项目独立于 OpenShell.Gui.Host.Tests 进程: 帧捕获会话与共享 [AvaloniaFact] 会话
/// 不能同进程混跑 (见 csproj 注释)。
/// </summary>
public class MainWindowScreenshotTests
{
    public static TheoryData<int, int> AcceptanceSizes { get; } = new()
    {
        { 1200, 800 },
        { 800, 500 },
    };

    [Theory]
    [MemberData(nameof(AcceptanceSizes))]
    public void MainWindow_Renders_At_Acceptance_Size(int width, int height)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaScreenshotApp));
        session.Dispatch(
            () => RenderAndCaptureAsync(width, height).GetAwaiter().GetResult(),
            CancellationToken.None);
    }

    private static async Task RenderAndCaptureAsync(int width, int height)
    {
        var vm = CreateMainViewModel();
        await vm.InitializeTabsAsync();

        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 强制目标尺寸布局并泵送渲染帧, 确保合成器出帧。
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        using var captured = window.GetLastRenderedFrame();
        captured.Should().NotBeNull("headless + Skia 渲染必须产出帧");

        var outDir = Path.Combine(FindRepoRoot(), "docs", "screenshots");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"desktop-{width}x{height}.png");
        captured!.Save(outPath);

        new FileInfo(outPath).Length.Should().BeGreaterThan(1024,
            $"{width}x{height} 渲染产物必须是有效位图, 而不是空白/损坏文件");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>构造带真实 Core 服务的工作区 ViewModel (与 Gui.Host.Tests 的 TestAppBuilder 同型)。</summary>
    private static MainViewModel CreateMainViewModel()
    {
        var providers = new ProviderRegistry();
        providers.Register(new FileSystemProvider());

        var commands = new CommandRegistry();
        var operations = new OperationEngine(providers);

        return new MainViewModel(
            providers,
            commands,
            operations,
            new StubDialogService(),
            new InMemoryTaskCenter(),
            new ItemPath { Provider = "fs", InternalPath = Path.GetTempPath().Replace('\\', '/') },
            new OpenShell.Errors.InMemoryErrorStream(),
            (_, _) => Task.CompletedTask,
            () => CancellationToken.None,
            i18n: null,
            sessionTabs: null);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenShell.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root (OpenShell.slnx) not found above test assembly.");
    }
}

/// <summary>
/// 帧捕获专用 headless App: Skia 真实渲染 (UseHeadlessDrawing=false)。
/// </summary>
internal sealed class SkiaScreenshotApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<SkiaScreenshotApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .UseReactiveUI()
            .WithInterFont();
    }
}

/// <summary>烟测用对话框桩: 全部返回取消, 截图流程不触发对话框。</summary>
internal sealed class StubDialogService : IDialogService
{
    public Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions options, CancellationToken ct = default)
        => Task.FromResult(DialogResult.Cancel);

    public Task<IReadOnlyList<ItemPath>?> ShowOpenFileDialogAsync(FileDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ItemPath>?>(null);

    public Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<ItemPath?>(null);

    public Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<ItemPath?>(null);

    public Task<string?> ShowInputAsync(InputDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct = default)
        => Task.FromResult<T?>(default);
}
