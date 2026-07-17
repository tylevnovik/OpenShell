using System;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using OpenShell.Items;

namespace OpenShell.Gui.Host.Views;

public partial class FileListView : UserControl
{
    private IItem? _contextMenuItem;

    public FileListView()
    {
        InitializeComponent();
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (DataContext is not ViewModels.BrowserTab tab) return;
        if (lb.SelectedItem is not IItem item) return;
        if (item.Kind is ItemKind.Directory or ItemKind.Container)
        {
            tab.Pane.NavigateCommand.Execute(item.Path).Subscribe();
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        _contextMenuItem = lb.SelectedItem as IItem;
        if (e.Source is Visual source && e.TryGetPosition(lb, out var pos))
        {
            if (lb is IInputElement inputElement)
            {
                var hit = inputElement.InputHitTest(pos);
                if (hit is Visual v)
                {
                    var lbi = v.FindAncestorOfType<ListBoxItem>();
                    if (lbi?.DataContext is IItem item)
                    {
                        if (!lb.SelectedItems?.Contains(item) ?? true)
                        {
                            lb.SelectedItems?.Clear();
                            lb.SelectedItems?.Add(item);
                        }
                        _contextMenuItem = item;
                    }
                }
            }
        }
    }
}
