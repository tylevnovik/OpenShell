using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FluentAssertions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Gui.Host.Views;
using Xunit;

namespace OpenShell.Gui.Host.Tests;

/// <summary>
/// IH-011: 桌面视觉验收门禁。
/// 以 Avalonia headless + 真实 Skia 渲染真实 <see cref="MainWindow"/>
/// 于 1200x800 与 800x500 两种验收尺寸, 输出 PNG 到 <c>docs/screenshots/</c> 作为人工复核素材,
/// 同时断言渲染流程无异常、产物非空——每次 CI 都会复跑该门禁。
/// 说明: headless + Skia 是像素级真实布局/绘制, 但不含真实桌面合成器/字体回退环境;
/// 三平台真实桌面复验仍按审计文档保留为人工步骤。
/// </summary>
public class DesktopVisualAcceptanceTests
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
        // 帧捕获要求 Skia 真实渲染 (UseHeadlessDrawing=false), 与常规 [AvaloniaFact]
        // 的 headless-drawing 会话冲突, 因此这里单独启动一个会话。
        using var session = HeadlessUnitTestSession.StartNew(typeof(SkiaScreenshotApp));
        session.Dispatch(
            () => RenderAndCaptureAsync(width, height).GetAwaiter().GetResult(),
            CancellationToken.None);
    }

    private static async Task RenderAndCaptureAsync(int width, int height)
    {
        var vm = TestAppBuilder.CreateMainViewModel();
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
/// 与 <see cref="TestApp"/> 分离, 避免影响常规 [AvaloniaFact] 行为。
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
