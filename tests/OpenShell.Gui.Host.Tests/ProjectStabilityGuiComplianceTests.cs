using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using OpenShell.Gui.Host.Views;
using OpenShell.I18n;
using OpenShell.TestUtils;

namespace OpenShell.Gui.Host.Tests;

/// <summary>真实 GUI 烟测发现项的稳定性合规测试。</summary>
public sealed class ProjectStabilityGuiComplianceTests
{
    [AvaloniaFact]
    public void MainWindow_LocalizesVisibleResourceKeys()
    {
        using var tempDir = new TempDir();
        var i18n = new ResourceI18nService(tempDir.FullPath);
        var window = new MainWindow(i18n)
        {
            DataContext = TestAppBuilder.CreateMainViewModel(i18n),
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            VisibleStrings(window).Should().NotContain(
                value => value.StartsWith("gui.", StringComparison.Ordinal),
                "用户界面不应直接显示 i18n 资源键");

            var navigationHeaders = TestAppBuilder.FindDescendants<TreeViewItem>(window)
                .Select(item => item.Header?.ToString())
                .ToList();
            navigationHeaders.Should().Contain(new[] { "快速访问", "此电脑", "网络" });

            i18n.SetLocale("en-US");
            TestAppBuilder.PumpDispatcher();

            VisibleStrings(window).Should().NotContain(
                value => value.StartsWith("gui.", StringComparison.Ordinal));
            TestAppBuilder.FindDescendants<TreeViewItem>(window)
                .Select(item => item.Header?.ToString())
                .Should().Contain(new[] { "Quick access", "This PC", "Network" });
            TestAppBuilder.FindDescendants<Button>(window)
                .Select(button => button.Content?.ToString())
                .Should().Contain("📁 New");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_AttachBindsFileListToActiveBrowserTab()
    {
        using var tempDir = new TempDir();
        var i18n = new ResourceI18nService(tempDir.FullPath);
        var viewModel = TestAppBuilder.CreateMainViewModel(i18n);
        var window = new MainWindow(i18n)
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            var fileList = TestAppBuilder.FindDescendants<FileListView>(window).Single();
            fileList.DataContext.Should().BeSameAs(viewModel.Tabs[viewModel.ActiveTabIndex]);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<string> VisibleStrings(Window window)
    {
        yield return window.Title ?? string.Empty;

        foreach (var text in TestAppBuilder.FindDescendants<TextBlock>(window))
        {
            if (!string.IsNullOrEmpty(text.Text))
                yield return text.Text;
        }

        foreach (var textBox in TestAppBuilder.FindDescendants<TextBox>(window))
        {
            if (textBox.Watermark is string watermark)
                yield return watermark;
        }

        foreach (var button in TestAppBuilder.FindDescendants<Button>(window))
        {
            if (button.Content is string content)
                yield return content;
        }

        foreach (var menuItem in TestAppBuilder.FindDescendants<MenuItem>(window))
        {
            if (menuItem.Header is string header)
                yield return header;
        }
    }
}
