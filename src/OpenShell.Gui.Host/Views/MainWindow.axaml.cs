using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Clipboard;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Completion;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Favorites;
using OpenShell.Gui.Abstractions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.History;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Preview;
using OpenShell.Providers;
using OpenShell.Recent;
using OpenShell.Themes;
using ReactiveUI;

namespace OpenShell.Gui.Host.Views;

public partial class MainWindow : Window
{
#pragma warning disable CS0414
    private bool _altPressed;
    private II18nService? _i18n;
    private IThemeService? _themeService;
    private IFavoritesService? _favoritesService;
    private IRecentService? _recentService;
    private IConfigurationService? _configService;
    private CompositeDisposable _disposables;
    private TabControl? _tabControl;
    private ListBox? _consoleOutputList;
    private object? _detailsPane;
    private object? _previewPane;
    private IItem? _contextMenuItem;
    private FileListView? _mainFileListView;
    private IDisposable? _menuVisibleSubscription;
    private IDisposable? _activeTabSubscription;
    private bool _ownsDataContext = true;
#pragma warning restore CS0414

    public MainWindow() : this(null)
    {
    }

    public MainWindow(II18nService? i18n)
    {
        _i18n = i18n;
        _disposables = new CompositeDisposable();
        _previewPane = null;
        _contextMenuItem = null;

        InitializeComponent();

        var services = Program.Services;
        _themeService = services?.GetService<IThemeService>();
        _configService = services?.GetService<IConfigurationService>();
        _favoritesService = services?.GetService<IFavoritesService>();
        _recentService = services?.GetService<IRecentService>();
        _i18n ??= services?.GetService<II18nService>();

        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
            ApplyTranslations();
        }

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;

        if (_themeService != null)
        {
            _themeService.Changed
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(theme => ApplyTheme(theme.Name))
                .DisposeWith(_disposables);
            ApplyTheme(_themeService.Current.Name);
        }

        LoadWindowRectFromConfig();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        WireUpControls();
        SyncActiveFileList();
        ApplyTranslations();
    }

    private void WireUpControls()
    {
        _tabControl = this.FindControl<TabControl>("MainTabControl");
        _detailsPane = this.FindControl<Control>("DetailsPaneControl");
        _previewPane = BuildPreviewPane();
        _consoleOutputList = this.FindControl<ListBox>("ConsoleOutputList");
        _mainFileListView = this.GetLogicalDescendants().OfType<FileListView>().FirstOrDefault();
    }

    private void SyncActiveFileList()
    {
        _mainFileListView ??= this.GetLogicalDescendants().OfType<FileListView>().FirstOrDefault();
        if (_mainFileListView is not null
            && DataContext is MainViewModel vm
            && vm.ActiveTabIndex >= 0
            && vm.ActiveTabIndex < vm.Tabs.Count)
        {
            _mainFileListView.DataContext = vm.Tabs[vm.ActiveTabIndex];
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _menuVisibleSubscription?.Dispose();
        _activeTabSubscription?.Dispose();
        _disposables.Clear();

        if (DataContext is MainViewModel vm)
        {
            _menuVisibleSubscription = vm.WhenAnyValue(x => x.MenuVisible)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(visible =>
                {
                    if (this.FindControl<Menu>("MainMenu") is { } menu)
                    {
                        menu.IsVisible = visible;
                    }
                });

            _activeTabSubscription = vm.WhenAnyValue(x => x.ActiveTabIndex)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(idx =>
                {
                    if (_mainFileListView != null && idx >= 0 && idx < vm.Tabs.Count)
                    {
                        _mainFileListView.DataContext = vm.Tabs[idx];
                    }
                });

            if (_mainFileListView != null && vm.ActiveTabIndex >= 0 && vm.ActiveTabIndex < vm.Tabs.Count)
            {
                _mainFileListView.DataContext = vm.Tabs[vm.ActiveTabIndex];
            }

            if (_themeService != null)
            {
                _themeService.Changed
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(theme => ApplyTheme(theme.Name))
                    .DisposeWith(_disposables);
            }

            Dispatcher.UIThread.Post(() =>
            {
                SyncActiveFileList();
                ApplyTranslations();
            });
        }
    }

    private void OnTabButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tab && DataContext is MainViewModel vm)
        {
            var idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0)
            {
                vm.ActiveTabIndex = idx;
            }
        }
    }

    private void OnTabCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tab && DataContext is MainViewModel vm)
        {
            vm.CloseTabCommand.Execute(tab).Subscribe();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
        {
            _altPressed = true;
        }

        var vm = DataContext as MainViewModel;
        var modifiers = e.KeyModifiers;

        // 窗口级快捷键优先于 TextBox 默认行为，确保地址栏和全局搜索随时可用。
        if (vm is not null && modifiers == KeyModifiers.Control && e.Key == Key.L)
        {
            EnterAddressBarEditMode();
            e.Handled = true;
            return;
        }
        if (vm is not null && modifiers == KeyModifiers.Alt && e.Key == Key.D)
        {
            EnterAddressBarEditMode();
            e.Handled = true;
            return;
        }
        if (modifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            FocusSearchBox();
            e.Handled = true;
            return;
        }
        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.F)
        {
            ShowGlobalSearchWindow();
            e.Handled = true;
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox)
        {
            if (e.Key == Key.Escape && vm?.ActivePane.IsAddressEditing == true)
            {
                vm.CancelAddressBarEdit();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.F5)
        {
            vm?.RefreshCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.T)
        {
            vm?.NewTabCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.W)
        {
            vm?.CloseTabCommand.Execute(null).Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.A)
        {
            vm?.SelectAllCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.C)
        {
            vm?.CopyCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.X)
        {
            vm?.CutCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.V)
        {
            vm?.PasteCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.Z)
        {
            vm?.UndoCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.Y)
        {
            vm?.RedoCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && modifiers == KeyModifiers.None)
        {
            vm?.QuickLookCommand.Execute(vm.ActivePane.SelectedItems.FirstOrDefault()).Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if ((modifiers & KeyModifiers.Shift) != 0)
                vm?.ForceDeleteCommand.Execute().Subscribe();
            else
                vm?.DeleteCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            vm?.RenameCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Alt && e.Key == Key.Enter)
        {
            vm?.PropertiesCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            vm?.NavigateUpCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Alt && e.Key == Key.Left)
        {
            vm?.NavigateBackCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Alt && e.Key == Key.Right)
        {
            vm?.NavigateForwardCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            vm?.AboutCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.P)
        {
            ShowCommandPaletteWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm?.DeselectAllCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e) => OnGlobalKeyUp(sender, e);

    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            if (_altPressed) ToggleMenuVisibility();
            _altPressed = false;
        }
    }

    private void EnterAddressBarEditMode()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ActivePane.AddressText = vm.ActivePane.CurrentLocation.Display;
            vm.ActivePane.IsAddressEditing = true;
            Dispatcher.UIThread.Post(() =>
            {
                var addressBox = this.GetLogicalDescendants().OfType<BreadcrumbBar>().FirstOrDefault()
                    ?.FindControl<TextBox>("AddressTextBox");
                addressBox?.Focus();
                addressBox?.SelectAll();
            });
        }
    }

    private void ExitAddressBarEditMode()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CancelAddressBarEdit();
        }
    }

    private Control? BuildDetailsPane()
    {
        return this.FindControl<Control>("DetailsPaneControl");
    }

    private void UpdateDetailsPane()
    {
    }

    private Control? BuildPreviewPane()
    {
        return this.FindControl<PreviewPane>("PreviewPaneControl");
    }

    private void FocusSearchBox()
    {
        var searchBox = this.GetLogicalDescendants().OfType<ToolBar>().FirstOrDefault()
            ?.FindControl<TextBox>("SearchBox");
        searchBox?.Focus();
        searchBox?.SelectAll();
    }

    private void LoadWindowRectFromConfig()
    {
        if (_configService == null) return;
        var config = _configService.Config;
        if (config.WindowWidth.HasValue) Width = config.WindowWidth.Value;
        if (config.WindowHeight.HasValue) Height = config.WindowHeight.Value;
        if (config.WindowX.HasValue && config.WindowY.HasValue)
        {
            Position = new PixelPoint((int)config.WindowX.Value, (int)config.WindowY.Value);
        }
        if (config.WindowMaximized == true)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowRectToConfig()
    {
        if (_configService == null) return;
        var config = _configService.Config;
        if (WindowState == WindowState.Maximized)
        {
            config.WindowMaximized = true;
        }
        else
        {
            config.WindowMaximized = false;
            config.WindowWidth = Width;
            config.WindowHeight = Height;
            config.WindowX = Position.X;
            config.WindowY = Position.Y;
        }
        _ = _configService.SaveAsync();
    }

    private void ShowCommandPaletteWindow()
    {
        var services = Program.Services;
        var commands = services?.GetService<ICommandRegistry>();
        var guiHost = services?.GetService<GuiHost>();
        var completion = services?.GetService<ICompletionProvider>();

        var commandPalette = new CommandPaletteWindow(_i18n);
        if (commands != null && guiHost != null)
        {
            Func<string, CancellationToken, Task> dispatchLine = (line, ct) => guiHost.DispatchAsync(line, ct);
            commandPalette.DataContext = new CommandPaletteViewModel(commands, dispatchLine, _i18n, completion);
        }
        commandPalette.ShowDialog(this);
    }

    private void ShowGlobalSearchWindow()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var services = Program.Services;
        var providers = services?.GetService<IProviderRegistry>();
        var commands = services?.GetService<ICommandRegistry>();
        var guiHost = services?.GetService<GuiHost>();
        if (providers is null || commands is null || guiHost is null)
            return;

        var searchWindow = new GlobalSearchWindow(_i18n)
        {
            DataContext = new GlobalSearchViewModel(
                providers,
                commands,
                guiHost,
                _i18n,
                item => vm.NavigateCommand.Execute(item.Path.GetParent()).Subscribe()),
        };
        searchWindow.ShowDialog(this);
    }

    private TreeViewItem MakeNavTreeItem(string label, object? icon = null)
    {
        var navPane = this.FindControl<NavigationPane>("NavPaneControl");
        if (navPane != null)
        {
            return navPane.MakeNavTreeItem(label, icon);
        }
        var item = new TreeViewItem
        {
            Header = label
        };
        ToolTip.SetTip(item, label);
        return item;
    }

    private void OnFileListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveWindowRectToConfig();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SaveWindowRectToConfig();
        _menuVisibleSubscription?.Dispose();
        _activeTabSubscription?.Dispose();
        if (_i18n is not null)
        {
            _i18n.LocaleChanged -= OnLocaleChanged;
        }
        if (_ownsDataContext && DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _disposables.Dispose();
    }

    private void OnLocaleChanged(object? sender, string locale)
        => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        if (_i18n is not null)
            ControlLocalizer.Apply(this, _i18n);
    }

    private void ToggleMenuVisibility()
    {
        if (DataContext is MainViewModel vm)
        {
            vm.MenuVisible = !vm.MenuVisible;
        }
    }

    private void ApplyTheme(string theme)
    {
        RequestedThemeVariant = theme.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        try
        {
            _themeService?.Apply(theme);
        }
        catch
        {
        }
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnNewWindowClicked(object? sender, RoutedEventArgs e)
    {
        var services = Program.Services;
        var guiHost = services?.GetService<GuiHost>();
        if (services is null || guiHost is null)
            return;

        var initialLocation = DataContext is MainViewModel vm
            ? vm.ActivePane.CurrentLocation
            : guiHost.CurrentLocation;
        var newVm = new MainViewModel(
            services.GetRequiredService<IProviderRegistry>(),
            services.GetRequiredService<ICommandRegistry>(),
            services.GetRequiredService<IOperationEngine>(),
            services.GetRequiredService<IDialogService>(),
            services.GetRequiredService<ITaskCenter>(),
            initialLocation,
            services.GetRequiredService<IErrorStream>(),
            guiHost.DispatchAsync,
            () => guiHost.CommandCancellation.Token,
            services.GetService<IClipboardService>(),
            services.GetService<IUndoService>(),
            services.GetService<IQuickLookWindow>(),
            services.GetService<IPreviewService>(),
            _i18n,
            sessionTabs: null);
        try
        {
            await newVm.InitializeTabsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            newVm.Dispose();
            await guiHost.WriteOutputLineAsync($"Failed to initialize the new window: {ex.Message}");
            return;
        }
        var window = new MainWindow(_i18n)
        {
            DataContext = newVm,
        };
        window.Show(this);
    }

    private void OnViewModeClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || sender is not MenuItem menuItem
            || menuItem.Tag is not string tag)
            return;

        var mode = tag switch
        {
            "gui.viewMode.icons" => ViewMode.Icons,
            "gui.viewMode.tiles" => ViewMode.Tiles,
            "gui.viewMode.list" => ViewMode.List,
            _ => ViewMode.Details,
        };
        vm.ViewMode = mode;
    }

    private void OnThemeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            var tag = menuItem.Tag as string;
            var theme = tag switch
            {
                "gui.theme.light" => "Light",
                "gui.theme.dark" => "Dark",
                _ => "System"
            };
            ApplyTheme(theme);
        }
    }

    private void OnAboutClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.AboutCommand.Execute().Subscribe();
        }
    }

    private void OnCommandPaletteClicked(object? sender, RoutedEventArgs e)
    {
        ShowCommandPaletteWindow();
    }

    private void OnToggleDetailsPane(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsDetailsPaneVisible = !vm.IsDetailsPaneVisible;
        }
    }

    private void OnTogglePreviewPane(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsPreviewPaneVisible = !vm.IsPreviewPaneVisible;
        }
    }
}
