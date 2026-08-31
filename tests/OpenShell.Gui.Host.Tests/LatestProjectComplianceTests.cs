#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAssertions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Gui.Host.Views;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Preview;
using Xunit;

namespace OpenShell.Gui.Host.Tests;

/// <summary>
/// 最新项目 GUI 可用性合规测试。
/// </summary>
public sealed class LatestProjectComplianceTests
{
    [AvaloniaFact]
    public void File_List_Selection_Synchronizes_To_Pane()
    {
        var window = new MainWindow { DataContext = TestAppBuilder.CreateMainViewModel() };
        var vm = (MainViewModel)window.DataContext;
        var item = Item.File(new ItemPath { Provider = "fs", InternalPath = "/selection-check.txt" });
        vm.ActivePane.Items.Add(item);
        TestAppBuilder.PumpDispatcher();

        var fileList = TestAppBuilder.FindDescendants<FileListView>(window).Single();
        var list = fileList.FindControl<ListBox>("InnerFileList");
        list.Should().NotBeNull();
        list!.SelectedItems!.Add(item);
        TestAppBuilder.PumpDispatcher();

        vm.ActivePane.SelectedItems.Should().ContainSingle().Which.Should().Be(item);
    }

    [AvaloniaFact]
    public async Task Preview_Pane_Renders_Selected_Item()
    {
        var vm = TestAppBuilder.CreateMainViewModel();
        var item = Item.File(new ItemPath { Provider = "fs", InternalPath = "/preview-check.txt" });
        vm.ActivePane.SelectedItems.Add(item);
        var pane = new PreviewPane(new StubPreviewService());
        pane.DataContext = vm;

        await pane.RefreshAsync();
        TestAppBuilder.PumpDispatcher();

        var content = pane.FindControl<ContentControl>("PreviewContent");
        content.Should().NotBeNull();
        content!.Content.Should().BeOfType<ScrollViewer>();
        pane.FindControl<TextBlock>("StatusText")!.Text.Should().Be("Preview ready");
    }

    [AvaloniaFact]
    public async Task Address_And_Global_Shortcuts_Trigger_Real_Actions()
    {
        var vm = TestAppBuilder.CreateMainViewModel();
        var window = new MainWindow { DataContext = vm };
        var handler = typeof(MainWindow).GetMethod(
            "OnWindowKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        handler.Should().NotBeNull();

        handler!.Invoke(window, new object?[]
        {
            window,
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.L,
                KeyModifiers = KeyModifiers.Control,
            },
        });

        vm.ActivePane.IsAddressEditing.Should().BeTrue();
        vm.ActivePane.AddressText = "fs::/";
        await vm.CommitAddressBarAsync();
        vm.ActivePane.IsAddressEditing.Should().BeFalse();
        vm.ActivePane.CurrentLocation.Should().Be(ItemPath.Parse("fs::/"));
    }

    [AvaloniaFact]
    public void File_List_Registers_Drag_And_Drop()
    {
        var window = new MainWindow { DataContext = TestAppBuilder.CreateMainViewModel() };
        var list = TestAppBuilder.FindDescendants<ListBox>(window)
            .Single(x => x.Name == "InnerFileList");

        DragDrop.GetAllowDrop(list).Should().BeTrue();
    }

    [AvaloniaFact]
    public void Navigation_Nodes_Carry_Usable_Paths()
    {
        var pane = new NavigationPane { DataContext = TestAppBuilder.CreateMainViewModel() };
        TestAppBuilder.PumpDispatcher();

        var nodes = TestAppBuilder.FindDescendants<TreeViewItem>(pane);
        nodes.Should().NotBeEmpty();
        nodes.Select(x => x.Tag).OfType<NavigationItem>()
            .Should().Contain(x => x.Path.HasValue);
        nodes.Select(x => x.Tag).OfType<string>()
            .Should().BeEmpty("导航节点必须携带可执行的 NavigationItem，而不是静态字符串");
    }

    [AvaloniaFact]
    public void View_Mode_And_Window_Menus_Change_Real_UI()
    {
        var vm = TestAppBuilder.CreateMainViewModel();
        var window = new MainWindow { DataContext = vm };
        TestAppBuilder.PumpDispatcher();
        var fileList = TestAppBuilder.FindDescendants<FileListView>(window).Single();
        var list = fileList.FindControl<ListBox>("InnerFileList")!;
        var detailsTemplate = list.ItemTemplate;

        vm.ViewMode = ViewMode.Icons;
        TestAppBuilder.PumpDispatcher();
        list.ItemTemplate.Should().NotBeSameAs(detailsTemplate);

        var iconsMenuItem = TestAppBuilder.FindDescendants<MenuItem>(window)
            .Single(x => x.Tag as string == "gui.viewMode.icons");
        var handler = typeof(MainWindow).GetMethod(
            "OnViewModeClicked",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        handler!.Invoke(window, new object?[] { iconsMenuItem, new RoutedEventArgs() });
        vm.ViewMode.Should().Be(ViewMode.Icons);
    }

    [AvaloniaFact]
    public void Global_Search_Exposes_Content_Toggle_And_Cancel_Control()
    {
        var searchWindowType = typeof(MainWindow).Assembly.GetType(
            "OpenShell.Gui.Host.Views.GlobalSearchWindow");
        searchWindowType.Should().NotBeNull();

        var window = (Window)Activator.CreateInstance(searchWindowType!, new object?[] { null })!;
        var includeContents = TestAppBuilder.FindDescendants<CheckBox>(window).Single();
        includeContents.Content.Should().Be("Search contents");

        typeof(GlobalSearchViewModel).GetProperty(nameof(GlobalSearchViewModel.IncludeContents))
            .Should().NotBeNull();
        typeof(GlobalSearchViewModel).GetProperty(nameof(GlobalSearchViewModel.CancelCommand))
            .Should().NotBeNull();
        typeof(GlobalSearchViewModel).GetProperty(nameof(GlobalSearchViewModel.IndexStatusText))
            .Should().NotBeNull();
        window.Close();
    }

    private sealed class StubPreviewService : IPreviewService
    {
        public bool CanPreview(IItem item) => true;

        public ValueTask<PreviewViewModel?> CreatePreviewAsync(
            IItem item, PreviewOptions options, CancellationToken ct = default)
            => ValueTask.FromResult<PreviewViewModel?>(
                new PreviewViewModel.Text("preview content", "txt", 1, false));
    }
}
