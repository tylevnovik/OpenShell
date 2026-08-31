using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Clipboard;
using OpenShell.Gui.Host.Services;
using OpenShell.Items;
using OpenShell.Gui.Host.ViewModels;
using ReactiveUI;

namespace OpenShell.Gui.Host.Views;

public partial class FileListView : UserControl
{
    private IItem? _contextMenuItem;
    private BrowserTab? _boundTab;
    private IDisposable? _viewModeSubscription;
    private bool _syncingSelection;
    private Point? _dragStart;
    private IItem? _dragItem;
    private bool _dragStarted;
    private readonly IDragDropService? _dragDropService;
    private bool _dropTargetRegistered;

    public FileListView()
    {
        InitializeComponent();
        _dragDropService = Program.Services?.GetService<IDragDropService>();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        InnerFileList.SelectionChanged += OnListSelectionChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundTab is not null)
        {
            _boundTab.Pane.SelectedItems.CollectionChanged -= OnPaneSelectionChanged;
            _viewModeSubscription?.Dispose();
        }

        _boundTab = DataContext as BrowserTab;
        if (_boundTab is null)
            return;

        _boundTab.Pane.SelectedItems.CollectionChanged += OnPaneSelectionChanged;
        _viewModeSubscription = _boundTab.Owner.WhenAnyValue(vm => vm.ViewMode)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(mode => Dispatcher.UIThread.Post(() =>
            {
                ApplyViewMode(mode);
                SyncListSelectionFromPane();
            }));
        Dispatcher.UIThread.Post(() =>
        {
            ApplyViewMode(_boundTab.Owner.ViewMode);
            SyncListSelectionFromPane();
        });
    }

    private void ApplyViewMode(ViewMode mode)
    {
        var resourceKey = mode switch
        {
            ViewMode.Icons => "IconsItemTemplate",
            ViewMode.Tiles => "TilesItemTemplate",
            ViewMode.List => "ListItemTemplate",
            _ => "DetailsItemTemplate",
        };

        if (Resources[resourceKey] is IDataTemplate template)
            InnerFileList.ItemTemplate = template;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_dropTargetRegistered || _dragDropService is not AvaloniaDragDropService dragDrop)
            return;

        dragDrop.RegisterDropTarget(
            InnerFileList,
            () => _boundTab?.Pane.CurrentLocation);
        _dropTargetRegistered = true;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _dragStart = null;
        _dragItem = null;
        _dragStarted = false;
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _boundTab is null)
            return;

        _syncingSelection = true;
        try
        {
            _boundTab.Pane.SelectedItems.Clear();
            foreach (var item in InnerFileList!.SelectedItems!.OfType<IItem>())
                _boundTab.Pane.SelectedItems.Add(item);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnPaneSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(SyncListSelectionFromPane);

    private void SyncListSelectionFromPane()
    {
        if (_boundTab is null || _syncingSelection)
            return;

        _syncingSelection = true;
        try
        {
            InnerFileList!.SelectedItems!.Clear();
            var visibleItems = InnerFileList.Items!.OfType<IItem>().ToHashSet();
            foreach (var item in _boundTab.Pane.SelectedItems)
            {
                if (visibleItems.Contains(item))
                    InnerFileList.SelectedItems!.Add(item);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private static IItem? FindItemAtPointer(PointerEventArgs e)
    {
        return e.Source is Visual visual
            ? visual.FindAncestorOfType<ListBoxItem>()?.DataContext as IItem
            : null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox
            || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
            return;

        var item = FindItemAtPointer(e);
        if (item is null)
            return;

        if (!listBox.SelectedItems!.Contains(item))
        {
            listBox.SelectedItems!.Clear();
            listBox.SelectedItems!.Add(item);
        }

        _dragItem = item;
        _dragStart = e.GetPosition(listBox);
        _dragStarted = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBox listBox
            || _dragStart is not { } start
            || _dragStarted
            || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed
            || _dragDropService is not AvaloniaDragDropService dragDrop)
            return;

        var current = e.GetPosition(listBox);
        var delta = current - start;
        if (delta.X * delta.X + delta.Y * delta.Y < 25)
            return;

        _dragStarted = true;
        var items = listBox.SelectedItems!.OfType<IItem>().ToList();
        if (items.Count == 0 && _dragItem is not null)
            items.Add(_dragItem);

        _ = StartDragAsync(dragDrop, items, e);
    }

    private async Task StartDragAsync(
        AvaloniaDragDropService dragDrop,
        IReadOnlyList<IItem> items,
        PointerEventArgs trigger)
    {
        try
        {
            await dragDrop.StartDragFromPointerAsync(
                items,
                trigger,
                OpenShell.Clipboard.DragDropEffects.Copy | OpenShell.Clipboard.DragDropEffects.Move);
        }
        catch (InvalidOperationException)
        {
            // Headless/不支持 OS 拖拽的平台没有原生拖拽循环，忽略该次手势。
        }
        finally
        {
            _dragStart = null;
            _dragItem = null;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        _dragItem = null;
        _dragStarted = false;
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
