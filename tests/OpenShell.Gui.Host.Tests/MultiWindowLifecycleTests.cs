using Avalonia.Headless.XUnit;
using FluentAssertions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Gui.Host.Views;
using Xunit;

namespace OpenShell.Gui.Host.Tests;

/// <summary>
/// IH-010: 多窗口生命周期合规测试。
/// 每个窗口必须持有独立的工作区 ViewModel; 关闭一个窗口不得破坏其他窗口的状态。
/// </summary>
public class MultiWindowLifecycleTests
{
    [AvaloniaFact]
    public void Windows_Do_Not_Share_DataContext()
    {
        var vm1 = TestAppBuilder.CreateMainViewModel();
        var vm2 = TestAppBuilder.CreateMainViewModel();
        var window1 = new MainWindow { DataContext = vm1 };
        var window2 = new MainWindow { DataContext = vm2 };

        window1.DataContext.Should().NotBeSameAs(window2.DataContext,
            "新窗口必须使用独立工作区, 不能复用同一个 ViewModel");
    }

    [AvaloniaFact]
    public void Workspace_State_Is_Isolated_Between_Windows()
    {
        var vm1 = TestAppBuilder.CreateMainViewModel();
        var vm2 = TestAppBuilder.CreateMainViewModel();
        _ = new MainWindow { DataContext = vm1 };
        _ = new MainWindow { DataContext = vm2 };

        var before = vm2.Tabs.Count;
        vm1.NewTabCommand.Execute().Subscribe();

        vm1.Tabs.Count.Should().Be(before + 1, "窗口 1 应能新建标签");
        vm2.Tabs.Count.Should().Be(before, "窗口 1 的操作不得泄漏到窗口 2 的工作区");
    }

    [AvaloniaFact]
    public void Closing_One_Window_Does_Not_Break_The_Other()
    {
        var vm1 = TestAppBuilder.CreateMainViewModel();
        var vm2 = TestAppBuilder.CreateMainViewModel();
        var window1 = new MainWindow { DataContext = vm1 };
        var window2 = new MainWindow { DataContext = vm2 };
        window1.Show();
        window2.Show();
        TestAppBuilder.PumpDispatcher();

        var tabCountBefore = vm2.Tabs.Count;
        window1.Close();
        TestAppBuilder.PumpDispatcher();

        window2.IsVisible.Should().BeTrue("关闭窗口 1 不应关闭或破坏窗口 2");
        window2.DataContext.Should().BeSameAs(vm2);
        vm2.Tabs.Count.Should().Be(tabCountBefore);

        // 窗口 2 的工作区在窗口 1 关闭后仍然可用。
        vm2.NewTabCommand.Execute().Subscribe();
        vm2.Tabs.Count.Should().Be(tabCountBefore + 1);

        window2.Close();
        TestAppBuilder.PumpDispatcher();
    }
}
