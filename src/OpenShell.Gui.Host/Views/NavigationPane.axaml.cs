using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Favorites;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Recent;

namespace OpenShell.Gui.Host.Views;

public partial class NavigationPane : UserControl
{
    private int _buildVersion;

    public NavigationPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (NavTree != null)
        {
            NavTree.SelectionChanged -= OnNavSelectionChanged;
        }

        NavTree!.Items.Clear();

        if (DataContext is not MainViewModel vm)
            return;

        var version = ++_buildVersion;
        var quickAccess = new NavigationItem { Label = "gui.nav.quickAccess" };
        foreach (var favorite in Program.Services?.GetService<IFavoritesService>()?.Favorites ?? [])
        {
            if (TryParsePath(favorite.Path) is { } path)
                quickAccess.Children.Add(new NavigationItem { Label = favorite.Name, Path = path });
        }

        foreach (var recent in Program.Services?.GetService<IRecentService>()?.Recent.Take(10) ?? [])
        {
            if (TryParsePath(recent.Path) is { } path)
                quickAccess.Children.Add(new NavigationItem { Label = path.Display, Path = path });
        }

        var thisPc = new NavigationItem
        {
            Label = "gui.nav.thisPc",
            Path = ItemPath.Parse("fs::/"),
        };
        AddMountedDrives(thisPc);
        // 无 DI（例如 headless 测试）时仍保留一个真实可导航的根节点，而不是虚构 C:/D:。
        if (thisPc.Children.Count == 0)
            thisPc.Children.Add(new NavigationItem { Label = "fs::/", Path = thisPc.Path });

        var network = new NavigationItem
        {
            Label = "gui.nav.network",
            // 当前没有独立 network Provider，至少让节点进入可浏览的 fs 根目录。
            Path = ItemPath.Parse("fs::/"),
        };

        var quickTree = MakeNavTreeItem(quickAccess);
        quickTree.IsExpanded = quickAccess.Children.Count > 0;
        var pcTree = MakeNavTreeItem(thisPc);
        pcTree.IsExpanded = true;
        NavTree.Items.Add(quickTree);
        NavTree.Items.Add(pcTree);
        NavTree.Items.Add(MakeNavTreeItem(network));
        NavTree.SelectionChanged += OnNavSelectionChanged;

        _ = LoadDrivesAsync(vm, thisPc, version);
    }

    public TreeViewItem MakeNavTreeItem(string label, object? icon = null)
        => MakeNavTreeItem(new NavigationItem { Label = label });

    private TreeViewItem MakeNavTreeItem(NavigationItem navigationItem)
    {
        var item = new TreeViewItem
        {
            Header = navigationItem.Label,
            Tag = navigationItem,
        };
        ToolTip.SetTip(item, navigationItem.Label);
        foreach (var child in navigationItem.Children)
            item.Items.Add(MakeNavTreeItem(child));
        return item;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && NavTree?.SelectedItem is TreeViewItem treeItem)
        {
            if (treeItem.Tag is NavigationItem navigationItem)
                vm.SelectedNavigationItem = navigationItem;
        }
    }

    private async Task LoadDrivesAsync(MainViewModel vm, NavigationItem thisPc, int version)
    {
        try
        {
            var registry = Program.Services?.GetService<IProviderRegistry>();
            var driveProvider = registry?.ResolveCapability<IDriveProvider>(ItemPath.Parse("fs::/"));
            if (driveProvider is null)
                return;

            var drives = await driveProvider.GetDrivesAsync();
            if (version != _buildVersion)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (version != _buildVersion || NavTree is null)
                    return;

                thisPc.Children.Clear();
                AddMountedDrives(thisPc);
                foreach (var drive in drives)
                {
                    if (thisPc.Children.Any(existing => existing.Path == drive.Root))
                        continue;

                    thisPc.Children.Add(ToNavigationItem(drive));
                }

                if (thisPc.Children.Count == 0)
                    thisPc.Children.Add(new NavigationItem { Label = "fs::/", Path = thisPc.Path });

                var pcTree = NavTree.Items.OfType<TreeViewItem>().ElementAtOrDefault(1);
                if (pcTree is null)
                    return;
                pcTree.Items.Clear();
                foreach (var child in thisPc.Children)
                    pcTree.Items.Add(MakeNavTreeItem(child));
                pcTree.IsExpanded = true;
            });
        }
        catch
        {
            // 驱动枚举失败不阻塞窗口；已存在的 fs::/ 回退节点仍可用。
        }
    }

    private static void AddMountedDrives(NavigationItem thisPc)
    {
        var registry = Program.Services?.GetService<IDriveRegistry>();
        foreach (var drive in registry?.Mounted ?? [])
        {
            if (thisPc.Children.Any(existing => existing.Path == drive.Root))
                continue;

            thisPc.Children.Add(ToNavigationItem(drive));
        }
    }

    private static NavigationItem ToNavigationItem(ProviderDrive drive)
        => new()
        {
            Label = string.IsNullOrWhiteSpace(drive.DisplayLabel) ? drive.Name : drive.DisplayLabel,
            Path = drive.Root,
        };

    private static ItemPath? TryParsePath(string text)
    {
        try { return ItemPath.Parse(text); }
        catch (ArgumentException) { return null; }
    }
}
