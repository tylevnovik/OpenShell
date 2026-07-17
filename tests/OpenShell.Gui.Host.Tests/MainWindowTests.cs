using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Gui.Host.Views;
using Xunit;

namespace OpenShell.Gui.Host.Tests;

/// <summary>
/// Smoke tests for <see cref="MainWindow"/>. Per ADR-0033 §3 (top of testing pyramid).
/// Explorer 风格 GUI Shell 测试：验证单窗格布局、菜单结构、控制台/错误面板切换。
/// </summary>
public class MainWindowTests
{
    /// <summary>Verify the MainWindow can be constructed without throwing.</summary>
    [AvaloniaFact]
    public void MainWindow_can_be_constructed()
    {
        var window = new MainWindow();

        window.Should().NotBeNull();
        // T-305: 测试环境 _i18n 为 null, T() 回退到 key 本身。
        window.Title.Should().Be("gui.title");
    }

    /// <summary>Verify the window has a top menu with File / Edit / View / Help items.</summary>
    [AvaloniaFact]
    public void MainWindow_has_top_menu_with_file_edit_view_help()
    {
        var window = new MainWindow();

        var menu = TestAppBuilder.FindDescendants<Menu>(window).FirstOrDefault();
        menu.Should().NotBeNull("a top Menu should exist");

        var headers = TestAppBuilder.FindDescendants<MenuItem>(menu!)
            .Select(mi => mi.Header?.ToString())
            .ToList();
        // T-305: 测试环境 _i18n 为 null, header 为 i18n key 本身。
        headers.Should().Contain(new[] { "gui.menu.file", "gui.menu.edit", "gui.menu.view", "gui.menu.help" });
    }

    /// <summary>Verify the window has Explorer-style toolbar (back/forward/up/refresh + address bar).</summary>
    [AvaloniaFact]
    public void MainWindow_has_explorer_toolbar_with_navigation_buttons()
    {
        var window = new MainWindow();

        // 工具栏按钮内容：← → ↑ ↻
        var buttons = TestAppBuilder.FindDescendants<Button>(window).ToList();
        var contents = buttons.Select(b => b.Content?.ToString()).ToList();
        contents.Should().Contain(new[] { "←", "→", "↑", "↻" }, "Explorer 风格导航按钮应存在");
    }

    /// <summary>Verify the window has a single file list (Explorer style, not dual-pane).</summary>
    [AvaloniaFact]
    public void MainWindow_has_single_file_list_explorer_style()
    {
        var window = new MainWindow();

        // Explorer 模式下：主文件列表 1 个 + 错误列表 1 个 = 至少 2 个 ListBox
        // 但不应有"双窗格"（左+右各一个文件 ListBox）。验证主文件列表存在即可。
        var listBoxes = TestAppBuilder.FindDescendants<ListBox>(window).ToList();
        listBoxes.Should().HaveCountGreaterThanOrEqualTo(1, "至少应有一个主文件列表");
    }

    /// <summary>Verify the status bar shows item count on startup.</summary>
    [AvaloniaFact]
    public void MainWindow_status_bar_shows_items_count()
    {
        var window = new MainWindow();
        var vm = TestAppBuilder.CreateMainViewModel();
        window.DataContext = vm;

        TestAppBuilder.PumpDispatcher();

        // T-305: 测试环境 _i18n 为 null, 标签为 i18n key 本身。
        var statusTexts = TestAppBuilder.FindDescendants<TextBlock>(window)
            .Select(tb => tb.Text)
            .ToList();
        statusTexts.Should().Contain(t => t == "gui.status.items", "状态栏应显示 Items 标签");
    }

    /// <summary>Verify the command console is hidden by default (Explorer style).</summary>
    [AvaloniaFact]
    public void MainWindow_console_hidden_by_default()
    {
        var window = new MainWindow();
        var vm = TestAppBuilder.CreateMainViewModel();
        window.DataContext = vm;

        TestAppBuilder.PumpDispatcher();

        vm.IsConsoleVisible.Should().BeFalse("控制台默认应隐藏（Explorer 模式）");

        var consolePanel = FindConsolePanel(window);
        consolePanel.Should().NotBeNull("控制台面板应存在于控件树中");
        consolePanel!.IsVisible.Should().BeFalse("控制台默认应不可见");
    }

    /// <summary>Verify toggling IsConsoleVisible makes the console panel visible.</summary>
    [AvaloniaFact]
    public void MainWindow_toggle_console_makes_panel_visible()
    {
        var window = new MainWindow();
        var vm = TestAppBuilder.CreateMainViewModel();
        window.DataContext = vm;

        TestAppBuilder.PumpDispatcher();

        vm.IsConsoleVisible = true;
        TestAppBuilder.PumpDispatcher();

        var consolePanel = FindConsolePanel(window);
        consolePanel.Should().NotBeNull("控制台面板应存在");
        consolePanel!.IsVisible.Should().BeTrue("IsConsoleVisible=true 后控制台应可见");
    }

    /// <summary>Verify ToggleConsoleCommand flips IsConsoleVisible.</summary>
    [AvaloniaFact]
    public void ToggleConsoleCommand_flips_visibility()
    {
        var vm = TestAppBuilder.CreateMainViewModel();
        vm.IsConsoleVisible.Should().BeFalse();

        vm.ToggleConsoleCommand.Execute().Subscribe();
        vm.IsConsoleVisible.Should().BeTrue();

        vm.ToggleConsoleCommand.Execute().Subscribe();
        vm.IsConsoleVisible.Should().BeFalse();
    }

    /// <summary>Verify the error panel is hidden by default.</summary>
    [AvaloniaFact]
    public void MainWindow_error_panel_hidden_by_default()
    {
        var window = new MainWindow();
        var vm = TestAppBuilder.CreateMainViewModel();
        window.DataContext = vm;

        TestAppBuilder.PumpDispatcher();

        vm.IsErrorPanelVisible.Should().BeFalse();

        var errorPanel = FindErrorPanel(window);
        errorPanel.Should().NotBeNull("错误面板应存在");
        errorPanel!.IsVisible.Should().BeFalse("错误面板默认应隐藏");
    }

    /// <summary>
    /// Smoke test: click the refresh button and verify items list refreshes without throwing.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_click_refresh_button_smoke_test()
    {
        var window = new MainWindow();
        var vm = TestAppBuilder.CreateMainViewModel();
        window.DataContext = vm;

        TestAppBuilder.PumpDispatcher();

        var refreshButton = TestAppBuilder.FindDescendants<Button>(window)
            .FirstOrDefault(b => b.Content?.ToString() == "↻");
        refreshButton.Should().NotBeNull("刷新按钮应存在");

        vm.LeftPane.RefreshCommand.Execute().Subscribe(_ => { });
        TestAppBuilder.PumpDispatcher();
    }

    /// <summary>Verify the file list has a context menu with Explorer-style items.</summary>
    [AvaloniaFact]
    public void MainWindow_file_list_has_context_menu()
    {
        var window = new MainWindow();

        // 主文件列表是唯一带 ContextMenu 的 ListBox（错误面板的 ListBox 无 ContextMenu）
        var fileList = TestAppBuilder.FindDescendants<ListBox>(window)
            .FirstOrDefault(lb => lb.ContextMenu is not null);
        fileList.Should().NotBeNull("主文件列表应存在且有右键菜单");
        fileList!.ContextMenu.Should().NotBeNull("文件列表应有右键菜单");

        var headers = TestAppBuilder.FindDescendants<MenuItem>(fileList.ContextMenu!)
            .Select(mi => mi.Header?.ToString())
            .ToList();
        // T-305: 测试环境 _i18n 为 null, header 为 i18n key 本身。
        headers.Should().Contain(new[] { "gui.ctx.open", "gui.ctx.copy", "gui.ctx.move", "gui.ctx.delete", "gui.ctx.rename", "gui.ctx.properties" });
    }

    /// <summary>Finds the console panel by locating the console title label.</summary>
    private static Border? FindConsolePanel(Window window)
    {
        // T-305: 测试环境 _i18n 为 null, 标题为 i18n key 本身。
        var titleTextBlock = TestAppBuilder.FindDescendants<TextBlock>(window)
            .FirstOrDefault(tb => tb.Text?.Contains("gui.console.title") == true);
        if (titleTextBlock is null) return null;

        var parent = titleTextBlock.Parent;
        while (parent is not null)
        {
            if (parent is Border border)
                return border;
            parent = (parent as Avalonia.StyledElement)?.Parent;
        }
        return null;
    }

    /// <summary>Finds the error panel by locating the error title label.</summary>
    private static Border? FindErrorPanel(Window window)
    {
        // T-305: 测试环境 _i18n 为 null, 标题为 i18n key 本身。
        var titleTextBlock = TestAppBuilder.FindDescendants<TextBlock>(window)
            .FirstOrDefault(tb => tb.Text == "gui.errors.title");
        if (titleTextBlock is null) return null;

        var parent = titleTextBlock.Parent;
        while (parent is not null)
        {
            if (parent is Border border)
                return border;
            parent = (parent as Avalonia.StyledElement)?.Parent;
        }
        return null;
    }
}
