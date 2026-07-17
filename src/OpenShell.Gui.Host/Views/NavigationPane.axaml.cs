using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Paths;

namespace OpenShell.Gui.Host.Views;

public partial class NavigationPane : UserControl
{
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

        if (DataContext is MainViewModel vm)
        {
            var quickAccess = MakeNavTreeItem("Quick access");
            quickAccess.Tag = "quickAccess";

            var thisPc = MakeNavTreeItem("This PC");
            thisPc.Tag = "thisPc";
            var cDrive = MakeNavTreeItem("Local Disk (C:)");
            var dDrive = MakeNavTreeItem("Local Disk (D:)");
            thisPc.Items.Add(cDrive);
            thisPc.Items.Add(dDrive);

            var network = MakeNavTreeItem("Network");
            network.Tag = "network";

            NavTree.Items.Add(quickAccess);
            NavTree.Items.Add(thisPc);
            NavTree.Items.Add(network);

            NavTree.SelectionChanged += OnNavSelectionChanged;
        }
    }

    public TreeViewItem MakeNavTreeItem(string label, object? icon = null)
    {
        var item = new TreeViewItem
        {
            Header = label,
            Tag = label
        };
        ToolTip.SetTip(item, label);
        return item;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && NavTree?.SelectedItem is TreeViewItem treeItem)
        {
            var tag = treeItem.Tag as string;
            var label = treeItem.Header as string ?? "";
            ItemPath? path = tag switch
            {
                "thisPc" => ItemPath.Parse("fs::/"),
                _ => null
            };
            vm.SelectedNavigationItem = new NavigationItem { Label = label, Path = path };
        }
    }
}
