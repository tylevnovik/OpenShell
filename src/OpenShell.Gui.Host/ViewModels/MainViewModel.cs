using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Clipboard;
using OpenShell.Errors;
using OpenShell.Gui.Abstractions;
using OpenShell.History;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>T-442: 文件列表视图模式。Per ADR-0013 §4.</summary>
public enum ViewMode
{
    /// <summary>详细列表（默认）：图标+名称+大小+类型+修改时间列。</summary>
    Details,
    /// <summary>大图标网格。</summary>
    Icons,
    /// <summary>平铺视图：图标+名称+简要信息。</summary>
    Tiles,
    /// <summary>简单列表：仅图标+名称。</summary>
    List,
}

/// <summary>V-18: 控制台输出条目类型——区分输入/输出/错误，用于着色显示。</summary>
public enum ConsoleEntryKind
{
    /// <summary>用户输入的命令行（前缀 "> "）。</summary>
    Input,
    /// <summary>命令正常输出。</summary>
    Output,
    /// <summary>命令执行错误。</summary>
    Error,
}

/// <summary>V-18: 控制台输出条目——一行文本 + 类型标记，供 ListBox + ItemTemplate 渲染。</summary>
public sealed record ConsoleEntry(string Text, ConsoleEntryKind Kind);

/// <summary>T-440: 浏览器标签页——每个 tab 持有独立的 PaneViewModel，实现多目录并行浏览。
/// 标签标题绑定到 CurrentLocation 的最后一段路径，导航时自动更新。</summary>
public sealed class BrowserTab : ReactiveViewModel
{
    private string _title;
    private readonly PaneViewModel _pane;

    public BrowserTab(PaneViewModel pane, string title)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _title = title;
        // 订阅 PaneViewModel.CurrentLocation 变化，自动更新标签标题
        pane.WhenAnyValue(x => x.CurrentLocation)
            .Subscribe(loc => Title = GetTitleFromPath(loc))
            .DisposeWith(Disposables);
    }

    /// <summary>标签页对应的 PaneViewModel。</summary>
    public PaneViewModel Pane => _pane;

    /// <summary>标签标题（显示在 TabItem.Header）。</summary>
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    /// <summary>从 ItemPath 提取标签标题（最后一段路径，类似 Explorer 标签）。</summary>
    private static string GetTitleFromPath(ItemPath path)
    {
        if (string.IsNullOrEmpty(path.InternalPath)) return path.Display;
        var parts = path.InternalPath.TrimEnd('/').Split('/');
        return parts.Length > 0 && !string.IsNullOrEmpty(parts[^1]) ? parts[^1] : path.Display;
    }
}

/// <summary>
/// 主窗口 ViewModel。Explorer 风格 GUI Shell（Per ADR-0013）。
/// T-440: 支持多标签页（TabControl），每个 tab 持有独立的 PaneViewModel。
/// 命令控制台通过 ToggleConsoleCommand 切换可见性，保留 shell 能力给高级用户。
/// ViewModel 不引用 Avalonia.* 命名空间（除 ApplicationLifetime 退出逻辑），可单测。
/// </summary>
public sealed class MainViewModel : ReactiveViewModel
{
    private readonly IProviderRegistry _providers;
    private readonly ICommandRegistry _commands;
    private readonly IOperationEngine _operations;
    private readonly IDialogService _dialogs;
    private readonly ITaskCenter _taskCenter;
    private readonly IErrorStream _errors;
    private readonly Func<string, CancellationToken, Task> _dispatchLine;
    private readonly Func<CancellationToken> _cancelTokenAccessor;
    private readonly II18nService? _i18n;
    // T-404/T-405/T-409: 新增服务注入（可空，测试环境可传 null）
    private readonly IClipboardService? _clipboard;
    private readonly IUndoService? _undo;
    private readonly IQuickLookWindow? _quickLook;

    // 导航历史栈（Explorer 风格 Back/Forward）。Per ADR-0013.
    private readonly Stack<ItemPath> _backStack = new();
    private readonly Stack<ItemPath> _forwardStack = new();
    private bool _isNavigatingInHistory;  // 防止历史导航时重复 push 当前路径
    private ItemPath _lastLocation;  // 记录上次路径，用于判断导航方向

    private bool _isConsoleVisible;
    private bool _isErrorPanelVisible;
    private bool _isProfileLoading = true;
    private string _commandInput = string.Empty;
    private string _commandOutput = string.Empty;
    // F-26: 操作后的状态消息（如"已复制 3 项到 D:\Folder"），显示在状态栏右端，数秒后自动清除
    private string _statusMessage = string.Empty;
    private int _unreadErrorCount;
    private int _taskCount;
    // T-442: 视图模式（Details/Icons/Tiles/List），默认 Details
    private ViewMode _viewMode = ViewMode.Details;
    // T-445: 属性侧边面板可见性（默认隐藏，Alt+Enter 或 View > Details Pane 切换）
    private bool _isDetailsPaneVisible;
    // T-446: 预览侧边面板可见性（默认隐藏，View > Preview Pane 切换）
    private bool _isPreviewPaneVisible;
    // T-440: 多标签页——当前活动标签索引
    private int _activeTabIndex;
    private bool _menuVisible;
    private string? _searchText;
    private object? _selectedNavigationItem;

    /// <summary>构造 MainViewModel。</summary>
    public MainViewModel(
        IProviderRegistry providers,
        ICommandRegistry commands,
        IOperationEngine operations,
        IDialogService dialogs,
        ITaskCenter taskCenter,
        ItemPath initialLocation,
        IErrorStream errors,
        Func<string, CancellationToken, Task> dispatchLine,
        Func<CancellationToken> cancelTokenAccessor,
        // T-404/T-405/T-409: 新增可选服务参数（测试环境可传 null）
        IClipboardService? clipboard = null,
        IUndoService? undo = null,
        IQuickLookWindow? quickLook = null,
        // T-448: 构造函数注入 II18nService，替代 Service Locator
        II18nService? i18n = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _taskCenter = taskCenter ?? throw new ArgumentNullException(nameof(taskCenter));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _dispatchLine = dispatchLine ?? throw new ArgumentNullException(nameof(dispatchLine));
        _cancelTokenAccessor = cancelTokenAccessor ?? throw new ArgumentNullException(nameof(cancelTokenAccessor));
        // T-448: 优先使用构造函数注入，回退到 Service Locator
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;
        _clipboard = clipboard;
        _undo = undo;
        _quickLook = quickLook;

        // 单 Pane 为主（Explorer 风格）。LeftPane 保留作为兼容字段，但 ActivePane 指向 LeftPane。
        LeftPane = new PaneViewModel(providers, initialLocation);
        RightPane = new PaneViewModel(providers, initialLocation);
        ActivePane = LeftPane;

        // T-440: 多标签页——初始化 Tabs 集合，第一个标签包裹 LeftPane
        Tabs = new ObservableCollection<BrowserTab>();
        var firstTab = new BrowserTab(LeftPane, GetTabTitle(initialLocation));
        Tabs.Add(firstTab);
        ActiveTabIndex = 0;

        NavigationItems = new ObservableCollection<NavigationItem>();
        InitializeNavigationItems();

        Statusbar = new StatusbarViewModel(taskCenter);
        Statusbar.UpdateFromPane(LeftPane);

        // T-440: 订阅 ActiveTabIndex 变化，切换 ActivePane 指向（在 Statusbar 初始化后）
        this.WhenAnyValue(x => x.ActiveTabIndex)
            .Subscribe(idx =>
            {
                if (idx >= 0 && idx < Tabs.Count)
                {
                    ActivePane = Tabs[idx].Pane;
                    Statusbar.UpdateFromPane(ActivePane);
                }
            })
            .DisposeWith(Disposables);

        // 订阅活动 Pane 变化刷新状态栏（T-440: 切换 tab 时也需要刷新）
        this.WhenAnyValue(x => x.ActivePane)
            .Subscribe(pane =>
            {
                if (pane is { } p) Statusbar.UpdateFromPane(p);
            })
            .DisposeWith(Disposables);

        // 订阅 Pane 变化刷新状态栏
        LeftPane.WhenAnyValue(x => x.CurrentLocation, x => x.Items.Count, x => x.SelectedItems.Count)
            .Subscribe(_ => Statusbar.UpdateFromPane(LeftPane))
            .DisposeWith(Disposables);

        // 订阅 Pane 路径变化，维护导航历史栈。Per ADR-0013.
        // 手动跟踪 previous：WhenAnyValue 在变化时触发，此时 ActivePane.CurrentLocation 已是新值，
        // 用 _lastLocation 记录变化前的值，然后更新为当前值。
        LeftPane.WhenAnyValue(x => x.CurrentLocation)
            .Subscribe(newLocation =>
            {
                if (!_isNavigatingInHistory && !Equals(_lastLocation, default(ItemPath)))
                {
                    _backStack.Push(_lastLocation);
                    _forwardStack.Clear();
                }
                _lastLocation = newLocation;
            })
            .DisposeWith(Disposables);

        // 订阅错误流：每条新错误计入未读计数。Per ADR-0026.
        Observable.FromEventPattern<ErrorRecord>(
            h => _errors.ErrorWritten += h,
            h => _errors.ErrorWritten -= h)
            .Subscribe(_ => { if (!IsErrorPanelVisible) UnreadErrorCount++; })
            .DisposeWith(Disposables);

        // 命令初始化
        // T-404: CopyCommand 改为剪贴板复制语义（Ctrl+C）；旧「复制到文件夹」功能移至 CopyToFolderCommand
        CopyCommand = ReactiveCommand.CreateFromTask(CopyToClipboardCoreAsync);
        CopyToFolderCommand = ReactiveCommand.CreateFromTask(CopyToFolderCoreAsync);
        CutCommand = ReactiveCommand.CreateFromTask(CutCoreAsync);
        PasteCommand = ReactiveCommand.CreateFromTask(PasteCoreAsync);
        CopyAsPathCommand = ReactiveCommand.CreateFromTask(CopyAsPathCoreAsync);
        MoveCommand = ReactiveCommand.CreateFromTask(MoveCoreAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteCoreAsync);
        OpenCommand = ReactiveCommand.CreateFromTask<IItem?>(OpenCoreAsync);
        RenameCommand = ReactiveCommand.CreateFromTask(RenameCoreAsync);
        // T-407: 新建文件夹命令
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderCoreAsync);
        // F-03: 新建文件命令
        NewFileCommand = ReactiveCommand.CreateFromTask(NewFileCoreAsync);
        // T-405: QuickLook 预览命令
        QuickLookCommand = ReactiveCommand.Create<IItem?>(item => QuickLookCore(item));
        // T-409: 撤销/重做命令
        UndoCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_undo is not null) await _undo.UndoAsync();
            await ActivePane.RefreshCommand.Execute();
        });
        RedoCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_undo is not null) await _undo.RedoAsync();
            await ActivePane.RefreshCommand.Execute();
        });
        // T-427: 取消全选 / 反向选择
        DeselectAllCommand = ReactiveCommand.Create(() => ActivePane.SelectedItems.Clear());
        InvertSelectionCommand = ReactiveCommand.Create(() =>
        {
            var selected = new HashSet<IItem>(ActivePane.SelectedItems);
            ActivePane.SelectedItems.Clear();
            foreach (var item in ActivePane.Items)
            {
                if (!selected.Contains(item))
                    ActivePane.SelectedItems.Add(item);
            }
        });

        RefreshCommand = ReactiveCommand.CreateFromTask(async () => await ActivePane.RefreshCommand.Execute());
        NavigateUpCommand = ReactiveCommand.CreateFromTask(async () => await ActivePane.NavigateUpAsync());
        NavigateBackCommand = ReactiveCommand.CreateFromTask(NavigateBackCoreAsync);
        NavigateForwardCommand = ReactiveCommand.CreateFromTask(NavigateForwardCoreAsync);
        NavigateCommand = ReactiveCommand.CreateFromTask<ItemPath>(async p => await ActivePane.NavigateToAsync(p));
        SelectAllCommand = ReactiveCommand.Create(SelectAllCore);
        ExitCommand = ReactiveCommand.Create(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
        AboutCommand = ReactiveCommand.CreateFromTask(AboutCoreAsync);
        ToggleConsoleCommand = ReactiveCommand.Create(() => { IsConsoleVisible = !IsConsoleVisible; });
        ShowErrorPanelCommand = ReactiveCommand.Create(() =>
        {
            IsErrorPanelVisible = !IsErrorPanelVisible;
            if (IsErrorPanelVisible) UnreadErrorCount = 0;
        });
        SubmitCommandInputCommand = ReactiveCommand.CreateFromTask(SubmitCommandInputCoreAsync);
        PropertiesCommand = ReactiveCommand.CreateFromTask(PropertiesCoreAsync);
        // D-23: Open with 对话框
        OpenWithCommand = ReactiveCommand.CreateFromTask<IItem?>(OpenWithCoreAsync);
        // D-11: Create shortcut
        CreateShortcutCommand = ReactiveCommand.CreateFromTask(CreateShortcutCoreAsync);
        // T-440: 新建标签页（Ctrl+T）——在当前标签位置打开新 tab，初始位置为当前 tab 的路径
        NewTabCommand = ReactiveCommand.Create(NewTabCore);
        // T-440: 关闭标签页（Ctrl+W）——至少保留一个 tab
        CloseTabCommand = ReactiveCommand.Create<BrowserTab?>(CloseTabCore);

        // ADR-0013 约束：所有 ReactiveCommand 必须有 ThrownExceptions 订阅
        CopyCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        CopyToFolderCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        CutCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        PasteCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        CopyAsPathCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        MoveCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        DeleteCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        OpenCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        RenameCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NewFolderCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NewFileCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        QuickLookCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        UndoCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        RedoCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        DeselectAllCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        InvertSelectionCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NavigateUpCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NavigateBackCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NavigateForwardCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NavigateCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        SelectAllCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        ExitCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        AboutCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        ToggleConsoleCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        ShowErrorPanelCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        SubmitCommandInputCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        PropertiesCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        OpenWithCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        CreateShortcutCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        NewTabCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        CloseTabCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
    }

    /// <summary>左 Pane（Explorer 模式下即主 Pane）。保留字段供兼容。</summary>
    public PaneViewModel LeftPane { get; }

    /// <summary>右 Pane（Explorer 模式下未使用，保留供未来双窗格模式）。</summary>
    public PaneViewModel RightPane { get; }

    /// <summary>活动 Pane（T-440: 切换 tab 时动态指向当前 tab 的 PaneViewModel）。</summary>
    public PaneViewModel ActivePane { get; private set; }

    /// <summary>T-440: 多标签页集合。每个 tab 持有独立的 PaneViewModel。</summary>
    public ObservableCollection<BrowserTab> Tabs { get; }

    /// <summary>T-440: 当前活动标签索引。切换时自动更新 ActivePane。</summary>
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set => this.RaiseAndSetIfChanged(ref _activeTabIndex, value);
    }

    /// <summary>状态栏。</summary>
    public StatusbarViewModel Statusbar { get; }

    /// <summary>命令控制台是否可见（Ctrl+` 切换，默认隐藏）。</summary>
    public bool IsConsoleVisible
    {
        get => _isConsoleVisible;
        set => this.RaiseAndSetIfChanged(ref _isConsoleVisible, value);
    }

    /// <summary>错误面板是否可见。Per ADR-0026.</summary>
    public bool IsErrorPanelVisible
    {
        get => _isErrorPanelVisible;
        set => this.RaiseAndSetIfChanged(ref _isErrorPanelVisible, value);
    }

    /// <summary>Profile 是否正在加载。Per ADR-0041.</summary>
    public bool IsProfileLoading
    {
        get => _isProfileLoading;
        set => this.RaiseAndSetIfChanged(ref _isProfileLoading, value);
    }

    /// <summary>T-442: 文件列表视图模式（Details/Icons/Tiles/List）。Per ADR-0013 §4.</summary>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set => this.RaiseAndSetIfChanged(ref _viewMode, value);
    }

    /// <summary>T-445: 属性侧边面板是否可见。Alt+Enter 或 View > Details Pane 切换。</summary>
    public bool IsDetailsPaneVisible
    {
        get => _isDetailsPaneVisible;
        set => this.RaiseAndSetIfChanged(ref _isDetailsPaneVisible, value);
    }

    /// <summary>T-446: 预览侧边面板是否可见。View > Preview Pane 切换。</summary>
    public bool IsPreviewPaneVisible
    {
        get => _isPreviewPaneVisible;
        set => this.RaiseAndSetIfChanged(ref _isPreviewPaneVisible, value);
    }

    /// <summary>命令输入框文本。</summary>
    public string CommandInput
    {
        get => _commandInput;
        set => this.RaiseAndSetIfChanged(ref _commandInput, value);
    }

    /// <summary>命令输出（旧：纯文本拼接。V-18 后仅保留向后兼容，实际显示用 ConsoleOutputLines）。</summary>
    public string CommandOutput
    {
        get => _commandOutput;
        set => this.RaiseAndSetIfChanged(ref _commandOutput, value);
    }

    /// <summary>V-18: 控制台输出行集合——ListBox + ItemTemplate 渲染，支持输入/输出/错误着色。</summary>
    public ObservableCollection<ConsoleEntry> ConsoleOutputLines { get; } = new();

    /// <summary>F-26: 操作后的状态消息（如"已复制 3 项到 D:\Folder"），显示在状态栏右端。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    /// <summary>菜单是否可见（Alt 键切换）。</summary>
    public bool MenuVisible
    {
        get => _menuVisible;
        set => this.RaiseAndSetIfChanged(ref _menuVisible, value);
    }

    /// <summary>搜索文本。</summary>
    public string? SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            if (ActivePane != null)
            {
                ActivePane.FilterText = value;
            }
        }
    }

    /// <summary>导航树节点集合。</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    /// <summary>导航树选中项。</summary>
    public object? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNavigationItem, value);
            if (value is NavigationItem item && item.Path is { } path)
            {
                ActivePane.NavigateCommand.Execute(path).Subscribe();
            }
        }
    }

    /// <summary>任务数量。</summary>
    public int TaskCount
    {
        get => _taskCount;
        set => this.RaiseAndSetIfChanged(ref _taskCount, value);
    }

    /// <summary>错误数量（转发到 UnreadErrorCount）。</summary>
    public int ErrorCount => UnreadErrorCount;

    /// <summary>显示错误面板命令（转发到 ShowErrorPanelCommand）。</summary>
    public ReactiveCommand<Unit, Unit> ShowErrorsCommand => ShowErrorPanelCommand;

    /// <summary>命令历史（最近 100 条）。</summary>
    public ObservableCollection<string> CommandHistory { get; } = new();

    /// <summary>错误列表。</summary>
    public ObservableCollection<ErrorRecord> Errors { get; } = new();

    /// <summary>未读错误数。</summary>
    public int UnreadErrorCount
    {
        get => _unreadErrorCount;
        set => this.RaiseAndSetIfChanged(ref _unreadErrorCount, value);
    }

    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    // T-404: 旧 CopyCommand（复制到文件夹）语义保留为 CopyToFolderCommand
    public ReactiveCommand<Unit, Unit> CopyToFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> CutCommand { get; }
    public ReactiveCommand<Unit, Unit> PasteCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyAsPathCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<IItem?, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameCommand { get; }
    // T-407: 新建文件夹
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    // F-03: 新建文件
    public ReactiveCommand<Unit, Unit> NewFileCommand { get; }
    // T-405: QuickLook 预览
    public ReactiveCommand<IItem?, Unit> QuickLookCommand { get; }
    // T-409: 撤销/重做
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    // T-427: 取消全选 / 反向选择
    public ReactiveCommand<Unit, Unit> DeselectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> InvertSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateUpCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateBackCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateForwardCommand { get; }
    public ReactiveCommand<ItemPath, Unit> NavigateCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConsoleCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowErrorPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> SubmitCommandInputCommand { get; }
    public ReactiveCommand<Unit, Unit> PropertiesCommand { get; }
    // D-23: Open with 对话框（选中文件用其他程序打开）
    public ReactiveCommand<IItem?, Unit> OpenWithCommand { get; }
    // D-11: Create shortcut（创建 .lnk 快捷方式）
    public ReactiveCommand<Unit, Unit> CreateShortcutCommand { get; }
    // T-440: 新建标签页（Ctrl+T）
    public ReactiveCommand<Unit, Unit> NewTabCommand { get; }
    // T-440: 关闭标签页（Ctrl+W），参数为要关闭的 BrowserTab（null 则关闭当前 tab）
    public ReactiveCommand<BrowserTab?, Unit> CloseTabCommand { get; }

    /// <summary>刷新 Errors 集合。</summary>
    public void RefreshErrors()
    {
        Errors.Clear();
        foreach (var e in _errors.RecentErrors) Errors.Add(e);
    }

    /// <summary>i18n 翻译辅助：通过当前 II18nService 翻译 key 并格式化；服务不可用时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    // ------------------------------------------------------------------
    // 导航历史栈
    // ------------------------------------------------------------------

    private async Task NavigateBackCoreAsync()
    {
        if (_backStack.Count == 0) return;
        var prev = _backStack.Pop();
        _forwardStack.Push(ActivePane.CurrentLocation);
        _isNavigatingInHistory = true;
        try
        {
            await ActivePane.NavigateToAsync(prev);
        }
        finally
        {
            _isNavigatingInHistory = false;
        }
    }

    private async Task NavigateForwardCoreAsync()
    {
        if (_forwardStack.Count == 0) return;
        var next = _forwardStack.Pop();
        _backStack.Push(ActivePane.CurrentLocation);
        _isNavigatingInHistory = true;
        try
        {
            await ActivePane.NavigateToAsync(next);
        }
        finally
        {
            _isNavigatingInHistory = false;
        }
    }

    private void SelectAllCore()
    {
        ActivePane.SelectedItems.Clear();
        foreach (var item in ActivePane.Items) ActivePane.SelectedItems.Add(item);
    }

    // ------------------------------------------------------------------
    // 文件操作命令
    // ------------------------------------------------------------------

    /// <summary>T-404: 复制选中项到剪贴板（Ctrl+C 语义）。</summary>
    private async Task CopyToClipboardCoreAsync()
    {
        if (ActivePane.SelectedItems.Count == 0) return;
        if (_clipboard is null)
        {
            // 剪贴板服务未注入，回退到旧的「复制到文件夹」行为
            await CopyToFolderCoreAsync();
            return;
        }
        await _clipboard.SetItemsAsync(ActivePane.SelectedItems.ToList(), cut: false);
    }

    /// <summary>T-404: 剪切选中项到剪贴板（Ctrl+X 语义）。</summary>
    private async Task CutCoreAsync()
    {
        if (ActivePane.SelectedItems.Count == 0) return;
        if (_clipboard is null) return;
        await _clipboard.SetItemsAsync(ActivePane.SelectedItems.ToList(), cut: true);
    }

    /// <summary>T-404: 粘贴剪贴板内容到当前目录（Ctrl+V 语义）。</summary>
    private async Task PasteCoreAsync()
    {
        if (_clipboard is null) return;
        var items = await _clipboard.GetItemsAsync();
        if (items is null || items.Count == 0) return;
        var dest = ActivePane.CurrentLocation;
        foreach (var item in items)
        {
            await _operations.CopyAsync(item.Path, dest.Combine(item.Name));
        }
        // 若是剪切模式，粘贴后删除源项
        if (_clipboard.WasCut)
        {
            foreach (var item in items)
            {
                await _operations.DeleteAsync(item.Path);
            }
        }
        await ActivePane.RefreshCommand.Execute();
    }

    /// <summary>T-427: 复制选中项路径到剪贴板（Copy as path）。</summary>
    private async Task CopyAsPathCoreAsync()
    {
        if (ActivePane.SelectedItems.FirstOrDefault() is not { } item) return;
        if (_clipboard is not null)
        {
            await _clipboard.SetTextAsync(item.Path.Display);
        }
    }

    /// <summary>T-404: 旧 CopyCommand 语义——弹出文件夹选择器复制到目标目录。</summary>
    private async Task CopyToFolderCoreAsync()
    {
        if (ActivePane.SelectedItems.Count == 0) return;
        var dest = await _dialogs.ShowFolderBrowserAsync(new FolderDialogOptions
        {
            Title = T("gui.dialog.copyTo"),
            InitialDirectory = ActivePane.CurrentLocation,
        });
        if (dest is null) return;
        var count = ActivePane.SelectedItems.Count;
        foreach (var item in ActivePane.SelectedItems)
        {
            await _operations.CopyAsync(item.Path, dest.Value.Combine(item.Name));
        }
        await ActivePane.RefreshCommand.Execute();
        // F-26: 操作后显示状态消息（选中保持由 PaneViewModel.RefreshCoreAsync 自动恢复）
        StatusMessage = T("gui.status.copiedN", count, dest.Value.Display);
    }

    /// <summary>T-407: 新建文件夹。</summary>
    private async Task NewFolderCoreAsync()
    {
        var name = await _dialogs.ShowInputAsync(new InputDialogOptions
        {
            Title = T("gui.dialog.newFolderTitle"),
            Label = T("gui.dialog.newFolderLabel"),
            DefaultValue = T("gui.dialog.newFolderTitle"),
            Validator = v => string.IsNullOrWhiteSpace(v) ? T("gui.dialog.nameEmpty") : null,
        });
        if (name is null) return;
        var dest = ActivePane.CurrentLocation.Combine(name);
        await _operations.CreateDirectoryAsync(dest);
        await ActivePane.RefreshCommand.Execute();
    }

    /// <summary>F-03: 新建文件。弹输入框输入文件名，创建空文件。</summary>
    private async Task NewFileCoreAsync()
    {
        var name = await _dialogs.ShowInputAsync(new InputDialogOptions
        {
            Title = T("gui.tool.newFile"),
            Label = T("gui.tool.newFile"),
            DefaultValue = "newfile.txt",
            Validator = v => string.IsNullOrWhiteSpace(v) ? T("gui.dialog.nameEmpty") : null,
        });
        if (name is null) return;
        var dest = ActivePane.CurrentLocation.Combine(name);
        await _operations.TouchAsync(dest);
        await ActivePane.RefreshCommand.Execute();
    }

    /// <summary>T-405: QuickLook 预览选中项。</summary>
    private void QuickLookCore(IItem? item)
    {
        if (item is null && ActivePane.SelectedItems.FirstOrDefault() is not { } selected) return;
        var target = item ?? ActivePane.SelectedItems.First();
        _quickLook?.Show(target, null);
    }

    private async Task MoveCoreAsync()
    {
        if (ActivePane.SelectedItems.Count == 0) return;
        var dest = await _dialogs.ShowFolderBrowserAsync(new FolderDialogOptions
        {
            Title = T("gui.dialog.moveTo"),
            InitialDirectory = ActivePane.CurrentLocation,
        });
        if (dest is null) return;
        var count = ActivePane.SelectedItems.Count;
        var names = ActivePane.SelectedItems.Select(i => i.Name).ToList();
        foreach (var item in ActivePane.SelectedItems)
        {
            await _operations.MoveAsync(item.Path, dest.Value.Combine(item.Name));
        }
        await ActivePane.RefreshCommand.Execute();
        // F-26: 移动后选中丢失（项已不在当前目录），显示状态消息告知用户
        StatusMessage = T("gui.status.movedN", count, dest.Value.Display);
    }

    private async Task DeleteCoreAsync()
    {
        if (ActivePane.SelectedItems.Count == 0) return;
        var names = string.Join(", ", ActivePane.SelectedItems.Select(i => i.Name));
        var result = await _dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = T("gui.dialog.deleteTitle"),
            Message = T("gui.dialog.deleteMessage", ActivePane.SelectedItems.Count, names),
            Kind = MessageBoxKind.Warning,
            Buttons = MessageBoxButtons.YesNo,
        });
        if (result != DialogResult.Yes) return;
        foreach (var item in ActivePane.SelectedItems)
        {
            await _operations.DeleteAsync(item.Path);
        }
        await ActivePane.RefreshCommand.Execute();
    }

    private async Task OpenCoreAsync(IItem? item)
    {
        if (item is null) return;
        // 目录则导航进入
        if (item.Kind is ItemKind.Directory or ItemKind.Container)
        {
            await ActivePane.NavigateToAsync(item.Path);
            return;
        }
        // 文件用系统默认应用打开
        if (item.Path.Provider != "fs")
        {
            // T-411: 失败写入错误流而非隐藏的 CommandOutput
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = T("gui.dialog.cannotOpenNonFs", item.Path.Display),
                Operation = "Open",
                TargetPath = item.Path,
            });
            return;
        }
        try
        {
            var localPath = item.Path.InternalPath.Replace('/', '\\');
            Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // T-411: 失败写入错误流而非隐藏的 CommandOutput
            _errors.Write(ErrorRecord.FromException(ex, operation: "Open", targetPath: item.Path));
        }
    }

    private async Task RenameCoreAsync()
    {
        if (ActivePane.SelectedItems.FirstOrDefault() is not { } item) return;
        if (string.IsNullOrEmpty(item.Name)) return;
        var newName = await _dialogs.ShowInputAsync(new InputDialogOptions
        {
            Title = T("gui.dialog.renameTitle"),
            Label = T("gui.dialog.renameLabel"),
            DefaultValue = item.Name,
            Validator = v => string.IsNullOrWhiteSpace(v) ? T("gui.dialog.nameEmpty") : null,
        });
        if (newName is null || newName == item.Name) return;
        await _operations.RenameAsync(item.Path, newName);
        await ActivePane.RefreshCommand.Execute();
    }

    private async Task AboutCoreAsync()
    {
        await _dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = T("gui.dialog.aboutTitle"),
            Message = T("gui.dialog.aboutMessage"),
            Kind = MessageBoxKind.Information,
            Buttons = MessageBoxButtons.OK,
        });
    }

    private async Task SubmitCommandInputCoreAsync()
    {
        var line = CommandInput?.Trim();
        if (string.IsNullOrEmpty(line)) return;
        CommandHistory.Add(line);
        while (CommandHistory.Count > 100) CommandHistory.RemoveAt(0);

        // V-18: 使用 ConsoleOutputLines 集合替代 StringBuilder 拼接，支持 ListBox + 着色
        ConsoleOutputLines.Add(new ConsoleEntry(line, ConsoleEntryKind.Input));
        // 限制历史行数，避免无限增长
        while (ConsoleOutputLines.Count > 200) ConsoleOutputLines.RemoveAt(0);

        try
        {
            await _dispatchLine(line, _cancelTokenAccessor());
            ConsoleOutputLines.Add(new ConsoleEntry(T("gui.console.ok"), ConsoleEntryKind.Output));
        }
        catch (Exception ex)
        {
            ConsoleOutputLines.Add(new ConsoleEntry(T("gui.console.error", ex.Message), ConsoleEntryKind.Error));
        }
        // 保留 CommandOutput 向后兼容（拼接最新内容）
        CommandOutput = string.Join("\n", ConsoleOutputLines.Select(e => e.Kind == ConsoleEntryKind.Input ? "> " + e.Text : e.Text));
        CommandInput = string.Empty;
    }

    private async Task PropertiesCoreAsync()
    {
        // T-445: 切换属性侧边面板可见性（优先）。无选中项时显示 MessageBox 作为 fallback。
        if (ActivePane.SelectedItems.Count == 0)
        {
            await _dialogs.ShowMessageBoxAsync(new MessageBoxOptions
            {
                Title = T("gui.dialog.propertiesTitle"),
                Message = T("gui.detailsPane.title"),
                Kind = MessageBoxKind.Information,
                Buttons = MessageBoxButtons.OK,
            });
            return;
        }
        IsDetailsPaneVisible = !IsDetailsPaneVisible;
    }

    /// <summary>D-23: Open with 对话框——调用 Windows shell 的 "Open with" 对话框。
    /// 仅支持 fs provider 的文件；目录和非 fs 项报错。</summary>
    private async Task OpenWithCoreAsync(IItem? item)
    {
        if (item is null)
        {
            // 从 ActivePane.SelectedItems 兜底
            item = ActivePane.SelectedItems.FirstOrDefault();
            if (item is null) return;
        }
        // 目录直接用 OpenCommand 导航
        if (item.Kind is ItemKind.Directory or ItemKind.Container)
        {
            await OpenCoreAsync(item);
            return;
        }
        if (item.Path.Provider != "fs")
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = T("gui.dialog.cannotOpenNonFs", item.Path.Display),
                Operation = "OpenWith",
                TargetPath = item.Path,
            });
            return;
        }
        try
        {
            var localPath = item.Path.InternalPath.Replace('/', '\\');
            // Windows: 用 rundll32 shell32.dll,OpenAs_RunDLL 调出"打开方式"对话框
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL \"{localPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _errors.Write(ErrorRecord.FromException(ex, operation: "OpenWith", targetPath: item.Path));
        }
    }

    /// <summary>D-11: Create shortcut——在当前目录创建 .lnk 快捷方式文件。
    /// 使用 Windows Script Host（wscript.exe）通过临时 VBS 脚本创建快捷方式。</summary>
    private async Task CreateShortcutCoreAsync()
    {
        var item = ActivePane.SelectedItems.FirstOrDefault();
        if (item is null)
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = T("gui.dialog.noSelectionForShortcut"),
                Operation = "CreateShortcut",
            });
            return;
        }
        if (item.Path.Provider != "fs")
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = T("gui.dialog.cannotShortcutNonFs", item.Path.Display),
                Operation = "CreateShortcut",
                TargetPath = item.Path,
            });
            return;
        }
        try
        {
            var targetPath = item.Path.InternalPath.Replace('/', '\\');
            var currentDir = ActivePane.CurrentLocation.InternalPath.Replace('/', '\\');
            var shortcutName = item.Name + ".lnk";
            var shortcutPath = System.IO.Path.Combine(currentDir, shortcutName);
            // 如果快捷方式已存在，追加序号
            var counter = 1;
            while (System.IO.File.Exists(shortcutPath))
            {
                shortcutName = $"{item.Name} ({counter}).lnk";
                shortcutPath = System.IO.Path.Combine(currentDir, shortcutName);
                counter++;
            }
            // 用 PowerShell 创建 .lnk（比 VBS 更可靠，且 PowerShell 在 Windows 上预装）
            var psScript = $"""
                $ws = New-Object -ComObject WScript.Shell
                $sc = $ws.CreateShortcut('{shortcutPath}')
                $sc.TargetPath = '{targetPath}'
                $sc.Save()
            """;
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(5000);
            await ActivePane.RefreshCommand.Execute();
        }
        catch (Exception ex)
        {
            _errors.Write(ErrorRecord.FromException(ex, operation: "CreateShortcut", targetPath: item.Path));
        }
    }

    // ==================================================================
    // T-440: 多标签页实现
    // ==================================================================

    /// <summary>T-440: 新建标签页——以当前活动 tab 的路径为初始位置创建新 tab 并切换过去。</summary>
    private void NewTabCore()
    {
        var initialLocation = ActivePane.CurrentLocation;
        var pane = new PaneViewModel(_providers, initialLocation);
        var title = GetTabTitle(initialLocation);
        var tab = new BrowserTab(pane, title);
        Tabs.Add(tab);
        ActiveTabIndex = Tabs.Count - 1;
        // 触发新 tab 的首次加载
        _ = pane.RefreshCommand.Execute();
    }

    /// <summary>T-440: 关闭标签页——至少保留一个 tab。参数为 null 时关闭当前 tab。</summary>
    private void CloseTabCore(BrowserTab? tab)
    {
        // 至少保留一个 tab
        if (Tabs.Count <= 1) return;
        tab ??= ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;
        if (tab is null) return;
        var idx = Tabs.IndexOf(tab);
        Tabs.RemoveAt(idx);
        // 调整 ActiveTabIndex
        if (idx < ActiveTabIndex)
        {
            ActiveTabIndex--;
        }
        else if (idx == ActiveTabIndex)
        {
            // 关闭的是当前 tab，切换到相邻 tab
            ActiveTabIndex = Math.Min(idx, Tabs.Count - 1);
        }
    }

    /// <summary>T-440: 从 ItemPath 提取标签标题（最后一段路径）。</summary>
    private static string GetTabTitle(ItemPath path)
    {
        if (string.IsNullOrEmpty(path.InternalPath)) return path.Display;
        var parts = path.InternalPath.TrimEnd('/').Split('/');
        return parts.Length > 0 && !string.IsNullOrEmpty(parts[^1]) ? parts[^1] : path.Display;
    }

    private void InitializeNavigationItems()
    {
        var quickAccess = new NavigationItem { Label = "快速访问" };
        var thisPc = new NavigationItem { Label = "此电脑", Path = ItemPath.Parse("fs::/") };
        var network = new NavigationItem { Label = "网络" };
        NavigationItems.Add(quickAccess);
        NavigationItems.Add(thisPc);
        NavigationItems.Add(network);
    }
}
