using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using OpenShell.Gui.Host.Services;
using OpenShell.Gui.Host.Views;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.I18n;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Providers.FileSystem;
using OpenShell.Sessions;
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

            var statusXaml = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "Views", "StatusBar.axaml"));
            statusXaml.Should().Contain("Classes=\"StatusBar\"").And.NotContain("Background=\"LightGray\"");

            var detailsXaml = File.ReadAllText(RepoFile("src", "OpenShell.Gui.Host", "Views", "DetailsPane.axaml"));
            detailsXaml.Should().NotContain("名称:").And.NotContain("路径:").And.NotContain("大小:");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task PaneState_DistinguishesEmptyDirectoryAndEmptyFilter()
    {
        using var tempDir = new TempDir();
        var providers = new ProviderRegistry();
        providers.Register(new FileSystemProvider());
        using var pane = new PaneViewModel(providers, new ItemPath
        {
            Provider = "fs",
            InternalPath = tempDir.FullPath.Replace('\\', '/'),
        });

        await pane.NavigateToAsync(pane.CurrentLocation);
        pane.ShowEmptyState.Should().BeTrue();
        pane.ShowFilterEmptyState.Should().BeFalse();

        File.WriteAllText(Path.Combine(tempDir.FullPath, "visible.txt"), "data");
        await pane.NavigateToAsync(pane.CurrentLocation);
        pane.HasVisibleItems.Should().BeTrue();

        pane.FilterText = "no-match";
        pane.ShowEmptyState.Should().BeFalse();
        pane.ShowFilterEmptyState.Should().BeTrue();
    }

    [Fact]
    public void FileContextMenu_BindsToMainCommands()
    {
        typeof(BrowserTab).GetProperty("Owner").Should().NotBeNull();
        var viewModel = TestAppBuilder.CreateMainViewModel();
        viewModel.Tabs[0].Owner.Should().BeSameAs(viewModel);

        var fileListXaml = File.ReadAllText(
            RepoFile("src", "OpenShell.Gui.Host", "Views", "FileListView.axaml"));
        fileListXaml.Should().Contain("Owner.OpenCommand")
            .And.Contain("Owner.CopyCommand")
            .And.Contain("Owner.DeleteCommand")
            .And.NotContain("#Root.DataContext.CopyCommand");
    }

    [Fact]
    public void ActiveTabSwitch_NotifiesActivePaneBindings()
    {
        var viewModel = TestAppBuilder.CreateMainViewModel();
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ActivePane))
                notifications++;
        };

        viewModel.NewTabCommand.Execute().Subscribe();

        viewModel.ActivePane.Should().BeSameAs(viewModel.Tabs[1].Pane);
        notifications.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Tabs_RestoreAndPersistThroughSessionService()
    {
        using var tempDir = new TempDir();
        var firstPath = Path.Combine(tempDir.FullPath, "first");
        var secondPath = Path.Combine(tempDir.FullPath, "second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);

        var sessionService = new JsonSessionService(tempDir.FullPath);
        await sessionService.LoadOrCreateAsync("gui-test");
        using (var sessionTabs = new SessionTabsService(sessionService))
        using (var viewModel = TestAppBuilder.CreateMainViewModel(sessionTabs: sessionTabs))
        {
            await viewModel.InitializeTabsAsync();
            await viewModel.Tabs[0].Pane.NavigateToAsync(ToFilePath(firstPath));
            viewModel.NewTabCommand.Execute().Subscribe();
            await viewModel.Tabs[1].Pane.NavigateToAsync(ToFilePath(secondPath));
            viewModel.ActiveTabIndex = 1;

            viewModel.FlushTabsToSession();
            await sessionTabs.FlushAsync();
        }

        sessionService.Current!.State.Tabs.Should().HaveCount(2);
        sessionService.Current.State.ActiveTabIndex.Should().Be(1);

        var reloadedSession = new JsonSessionService(tempDir.FullPath);
        await reloadedSession.LoadOrCreateAsync("gui-test");
        using var reloadedTabs = new SessionTabsService(reloadedSession);
        using var restored = TestAppBuilder.CreateMainViewModel(sessionTabs: reloadedTabs);
        await restored.InitializeTabsAsync();

        restored.Tabs.Should().HaveCount(2);
        restored.ActiveTabIndex.Should().Be(1);
        restored.Tabs[0].Pane.CurrentLocation.Should().Be(ToFilePath(firstPath));
        restored.Tabs[1].Pane.CurrentLocation.Should().Be(ToFilePath(secondPath));
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

    private static ItemPath ToFilePath(string path) => new()
    {
        Provider = "fs",
        InternalPath = path.Replace('\\', '/'),
    };
}
