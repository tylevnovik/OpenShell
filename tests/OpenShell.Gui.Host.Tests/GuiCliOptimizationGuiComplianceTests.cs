using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using OpenShell.Gui.Host.Views;
using OpenShell.I18n;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Gui.Host.Tests;

/// <summary>GUI/CLI 产品化主题中的 GUI 合规测试。</summary>
public sealed class GuiCliOptimizationGuiComplianceTests
{
    [AvaloniaFact]
    public void ExistingExplorerWorkflow_RemainsAvailable()
    {
        var window = new MainWindow
        {
            DataContext = TestAppBuilder.CreateMainViewModel(),
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            window.MinWidth.Should().BeGreaterThanOrEqualTo(800);
            window.MinHeight.Should().BeGreaterThanOrEqualTo(500);
            TestAppBuilder.FindDescendants<FileListView>(window).Should().ContainSingle();
            TestAppBuilder.FindDescendants<TreeView>(window).Should().ContainSingle();
            var hasSearchBox = TestAppBuilder.FindDescendants<TextBox>(window)
                .Select(box => box.Watermark?.ToString() ?? string.Empty)
                .Any(watermark => watermark.Contains("search", StringComparison.OrdinalIgnoreCase)
                    || watermark.Contains("搜索", StringComparison.Ordinal));
            hasSearchBox.Should().BeTrue("主窗口应保留搜索入口");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void App_LoadsSemanticDesignResources()
    {
        var appXaml = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "App.axaml"));
        var colors = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "Styles", "Colors.axaml"));

        appXaml.Should().Contain("Styles/Colors.axaml")
            .And.Contain("Styles/Controls.axaml")
            .And.Contain("Styles/Icons.axaml");
        colors.Should().Contain("ShellSurfaceBrush")
            .And.Contain("ShellContentSurfaceBrush")
            .And.Contain("ShellTextPrimaryBrush")
            .And.Contain("ShellFocusBrush")
            .And.Contain("ShellDangerBrush");
    }

    [AvaloniaFact]
    public void HiddenDetailsPane_CollapsesWorkspaceColumn()
    {
        var window = new MainWindow
        {
            DataContext = TestAppBuilder.CreateMainViewModel(),
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            var workspace = window.FindControl<Grid>("WorkspaceGrid");
            workspace.Should().NotBeNull();
            workspace!.ColumnDefinitions[^1].ActualWidth.Should().Be(0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Toolbar_UsesAccessibleVectorCommands()
    {
        var window = new MainWindow
        {
            DataContext = TestAppBuilder.CreateMainViewModel(),
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            var toolbar = TestAppBuilder.FindDescendants<OpenShell.Gui.Host.Views.ToolBar>(window).Single();
            var commandButtons = TestAppBuilder.FindDescendants<Button>(toolbar)
                .Where(button => button.Classes.Contains("ToolBarButton"))
                .ToList();
            commandButtons.Should().NotBeEmpty();
            commandButtons.All(button => button.Content is not string text || text.Length > 1)
                .Should().BeTrue("熟悉命令应使用矢量图标，文本只用于需要消歧的命令");
            commandButtons.All(button => ToolTip.GetTip(button) is not null)
                .Should().BeTrue("图标命令必须提供工具提示");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TabStrip_ExposesActiveAndNewTabStates()
    {
        var viewModel = TestAppBuilder.CreateMainViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            window.FindControl<Button>("NewTabButton").Should().NotBeNull();
            viewModel.Tabs.Should().ContainSingle(tab => tab.IsActive);

            var mainWindowXaml = File.ReadAllText(
                RepoFile("src", "OpenShell.Gui.Host", "Views", "MainWindow.axaml"));
            mainWindowXaml.Should().Contain("Classes.ActiveTab=\"{Binding IsActive}\"");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = "pending T-612")]
    public void FileWorkspace_ExposesCompleteStates()
    {
        var window = new MainWindow
        {
            DataContext = TestAppBuilder.CreateMainViewModel(),
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            var fileList = TestAppBuilder.FindDescendants<FileListView>(window).Single();
            fileList.FindControl<Control>("EmptyStatePanel").Should().NotBeNull();
            fileList.FindControl<Control>("FilterEmptyStatePanel").Should().NotBeNull();
            fileList.FindControl<Control>("ErrorStatePanel").Should().NotBeNull();
            fileList.FindControl<Button>("RetryButton").Should().NotBeNull();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = "pending T-612")]
    public void StatusAndDetails_AreCompleteAndLocalized()
    {
        using var tempDir = new TempDir();
        var i18n = new ResourceI18nService(tempDir.FullPath);
        i18n.SetLocale("en-US");
        var window = new MainWindow(i18n)
        {
            DataContext = TestAppBuilder.CreateMainViewModel(i18n),
        };

        try
        {
            window.Show();
            TestAppBuilder.PumpDispatcher();

            var status = TestAppBuilder.FindDescendants<StatusBar>(window).Single();
            status.FindControl<TextBlock>("SelectedSizeText").Should().NotBeNull();
            status.FindControl<Border>("PART_Border")!.Background.Should().NotBe(Brushes.LightGray);

            var detailsXaml = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "Views", "DetailsPane.axaml"));
            detailsXaml.Should().NotContain("名称:").And.NotContain("路径:").And.NotContain("大小:");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact(Skip = "pending T-613")]
    public void Tabs_RestoreAndPersistThroughSessionService()
    {
        var appSource = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "App.cs"));
        var viewModelSource = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "ViewModels", "MainViewModel.cs"));

        appSource.Should().Contain("SessionTabsService");
        viewModelSource.Should().Contain("LoadTabsFromSessionAsync")
            .And.Contain("UpdateTabs");
    }

    [AvaloniaFact(Skip = "pending T-614")]
    public void InteractiveControls_HaveAccessibleNamesAndFocusStyles()
    {
        var controls = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "Styles", "Controls.axaml"));
        var toolbar = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "Views", "ToolBar.axaml"));

        controls.Should().Contain(":focus-visible").And.Contain("ShellFocusBrush");
        toolbar.Should().Contain("AutomationProperties.Name");
    }

    private static string RepoFile(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(TestDataPaths.Root, "..", ".."));
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }
}
