using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using OpenShell;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using ReactiveUI;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// 单个目录视图 ViewModel。Per ADR-0013 §3.
/// 双窗格各持一个 PaneViewModel，独立 CurrentLocation。
/// ViewModel 不引用 Avalonia.* 命名空间，可在控制台测试项目跑单测。
/// </summary>
public sealed class PaneViewModel : ReactiveViewModel
{
    private readonly IProviderRegistry _providers;
    private ItemPath _currentLocation;
    private bool _isLoading;
    private string? _errorMessage;
    private SortColumn _sortColumn = SortColumn.Name;
    private SortDirection _sortDirection = SortDirection.Ascending;
    // T-406: 搜索过滤文本。null/空表示无过滤。
    private string? _filterText;
    // T-406: 过滤前的完整列表（过滤清除时恢复）
    private List<IItem> _allItems = new();
    private bool _isAddressEditing;
    private string? _addressText;
    private int _itemCount;
    private int _selectedCount;
    private bool _hasLoaded;

    /// <summary>构造 PaneViewModel。</summary>
    /// <param name="providers">Provider 注册表，用于解析 IContainerProvider。</param>
    /// <param name="initialLocation">初始位置。</param>
    public PaneViewModel(IProviderRegistry providers, ItemPath initialLocation)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _currentLocation = initialLocation;

        Items = new ObservableCollection<IItem>();
        SelectedItems = new ObservableCollection<IItem>();
        BreadcrumbSegments = new ObservableCollection<BreadcrumbSegment>();

        Items.CollectionChanged += (_, _) =>
        {
            ItemCount = Items.Count;
            RaiseViewStateChanged();
        };
        SelectedItems.CollectionChanged += (_, _) =>
        {
            SelectedCount = SelectedItems.Count;
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(PrimarySelectedItem));
        };

        ItemCount = Items.Count;
        SelectedCount = SelectedItems.Count;

        // RefreshCommand：重新枚举 CurrentLocation。Per ADR-0013 §3.
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshCoreAsync);

        // NavigateCommand：接收 ItemPath，更新 CurrentLocation 后刷新。Per ADR-0013 §3.
        NavigateCommand = ReactiveCommand.CreateFromTask<ItemPath>(async path =>
        {
            await NavigateToAsync(path);
        });

        // DoubleClickCommand：双击列表项时触发。目录则进入，文件忽略。Per ADR-0013 §3.
        DoubleClickCommand = ReactiveCommand.CreateFromTask<IItem?>(async item =>
        {
            if (item is { Kind: ItemKind.Directory or ItemKind.Container })
            {
                await NavigateToAsync(item.Path);
            }
        });

        // SortCommand：按指定列切换排序方向（同列再点切换升降序，换列默认升序）
        SortCommand = ReactiveCommand.Create<SortColumn>(col =>
        {
            if (_sortColumn == col)
            {
                _sortDirection = _sortDirection == SortDirection.Ascending
                    ? SortDirection.Descending : SortDirection.Ascending;
            }
            else
            {
                _sortColumn = col;
                _sortDirection = SortDirection.Ascending;
            }
            ApplySort();
        });

        // ADR-0013 约束：所有 ReactiveCommand 必须有 ThrownExceptions 订阅，否则异常静默。
        RefreshCommand.ThrownExceptions
            .Subscribe(ex => ErrorMessage = ex.Message)
            .DisposeWith(Disposables);
        NavigateCommand.ThrownExceptions
            .Subscribe(ex => ErrorMessage = ex.Message)
            .DisposeWith(Disposables);
        DoubleClickCommand.ThrownExceptions
            .Subscribe(ex => ErrorMessage = ex.Message)
            .DisposeWith(Disposables);
        SortCommand.ThrownExceptions
            .Subscribe(ex => ErrorMessage = ex.Message)
            .DisposeWith(Disposables);

        UpdateBreadcrumb();
    }

    /// <summary>当前路径。独立维护，双窗格不共享。Per ADR-0013 §3.</summary>
    public ItemPath CurrentLocation
    {
        get => _currentLocation;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentLocation, value);
            UpdateBreadcrumb();
        }
    }

    /// <summary>面包屑导航段集合。</summary>
    public ObservableCollection<BreadcrumbSegment> BreadcrumbSegments { get; }

    /// <summary>是否正在编辑地址栏。</summary>
    public bool IsAddressEditing
    {
        get => _isAddressEditing;
        set => this.RaiseAndSetIfChanged(ref _isAddressEditing, value);
    }

    /// <summary>地址栏编辑文本。</summary>
    public string? AddressText
    {
        get => _addressText;
        set => this.RaiseAndSetIfChanged(ref _addressText, value);
    }

    /// <summary>当前目录项数。</summary>
    public int ItemCount
    {
        get => _itemCount;
        private set => this.RaiseAndSetIfChanged(ref _itemCount, value);
    }

    /// <summary>选中项数。</summary>
    public int SelectedCount
    {
        get => _selectedCount;
        private set => this.RaiseAndSetIfChanged(ref _selectedCount, value);
    }

    private void UpdateBreadcrumb()
    {
        BreadcrumbSegments.Clear();
        var segments = new List<BreadcrumbSegment>();
        var current = CurrentLocation;

        while (true)
        {
            var name = current.GetName();
            if (string.IsNullOrEmpty(name) || name == "/")
            {
                segments.Add(new BreadcrumbSegment { Label = "fs::", Path = current });
                break;
            }
            segments.Add(new BreadcrumbSegment { Label = name, Path = current });
            var parent = current.GetParent();
            if (parent.Display == current.Display)
                break;
            current = parent;
        }

        segments.Reverse();
        foreach (var seg in segments)
        {
            BreadcrumbSegments.Add(seg);
        }
    }

    /// <summary>是否正在加载目录（控制 spinner 显示）。Per ADR-0015 §4.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            RaiseViewStateChanged();
        }
    }

    /// <summary>加载错误信息（控制 error panel 显示）。null 表示无错误。Per ADR-0015 §4.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            RaiseViewStateChanged();
        }
    }

    /// <summary>当前目录的子项。绑定到 ListBox.ItemsSource。Per ADR-0013 §3.</summary>
    public ObservableCollection<IItem> Items { get; }

    /// <summary>当前选中的项。绑定到 ListBox.SelectedItems。Per ADR-0013 §3.</summary>
    public ObservableCollection<IItem> SelectedItems { get; }

    /// <summary>是否至少完成过一次枚举，避免启动瞬间短暂显示空目录状态。</summary>
    public bool HasLoaded => _hasLoaded;

    /// <summary>当前是否有可显示项目。</summary>
    public bool HasVisibleItems => Items.Count > 0;

    /// <summary>首次或空列表加载时显示居中加载状态。</summary>
    public bool ShowLoadingState => IsLoading && Items.Count == 0;

    /// <summary>当前目录本身为空，而不是被筛选为空。</summary>
    public bool ShowEmptyState => HasLoaded
        && !IsLoading
        && string.IsNullOrEmpty(ErrorMessage)
        && _allItems.Count == 0;

    /// <summary>目录有内容，但当前筛选没有匹配项。</summary>
    public bool ShowFilterEmptyState => HasLoaded
        && !IsLoading
        && string.IsNullOrEmpty(ErrorMessage)
        && _allItems.Count > 0
        && Items.Count == 0
        && !string.IsNullOrWhiteSpace(FilterText);

    /// <summary>是否存在可向用户展示并重试的加载错误。</summary>
    public bool ShowErrorState => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>是否存在选中项。</summary>
    public bool HasSelection => SelectedItems.Count > 0;

    /// <summary>详情面板使用的主选中项，避免在 XAML 中直接索引空集合。</summary>
    public IItem? PrimarySelectedItem => SelectedItems.FirstOrDefault();

    /// <summary>T-406: 搜索过滤文本。设置后自动过滤 Items。null/空表示无过滤。</summary>
    public string? FilterText
    {
        get => _filterText;
        set
        {
            this.RaiseAndSetIfChanged(ref _filterText, value);
            ApplyFilter();
            RaiseViewStateChanged();
        }
    }

    /// <summary>刷新当前目录。Per ADR-0013 §3.</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>导航到指定路径。Per ADR-0013 §3.</summary>
    public ReactiveCommand<ItemPath, Unit> NavigateCommand { get; }

    /// <summary>双击列表项时触发。Per ADR-0013 §3.</summary>
    public ReactiveCommand<IItem?, Unit> DoubleClickCommand { get; }

    /// <summary>按指定列排序（点击列头触发）。Per ADR-0013 §3.</summary>
    public ReactiveCommand<SortColumn, Unit> SortCommand { get; }

    /// <summary>当前排序列。</summary>
    public SortColumn SortColumn
    {
        get => _sortColumn;
        private set => this.RaiseAndSetIfChanged(ref _sortColumn, value);
    }

    /// <summary>当前排序方向。</summary>
    public SortDirection SortDirection
    {
        get => _sortDirection;
        private set => this.RaiseAndSetIfChanged(ref _sortDirection, value);
    }

    /// <summary>导航到指定路径并刷新。外部直接调用以支持路径栏 Enter 键等场景。</summary>
    public async Task NavigateToAsync(ItemPath path)
    {
        CurrentLocation = path;
        await RefreshCoreAsync();
    }

    /// <summary>D-27: 从持久化状态加载排序。直接设置排序字段并应用，不通过 SortCommand（避免 toggle 逻辑）。</summary>
    public void LoadSortState(SortColumn column, SortDirection direction)
    {
        _sortColumn = column;
        _sortDirection = direction;
        this.RaisePropertyChanged(nameof(SortColumn));
        this.RaisePropertyChanged(nameof(SortDirection));
        ApplySort();
    }

    /// <summary>导航到父目录。Per ADR-0027 (Backspace / Alt+Up)。</summary>
    public async Task NavigateUpAsync()
    {
        var parent = CurrentLocation.GetParent();
        if (parent.Display != CurrentLocation.Display)
        {
            await NavigateToAsync(parent);
        }
    }

    private async Task RefreshCoreAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var container = _providers.ResolveCapability<IContainerProvider>(CurrentLocation);
            if (container is null)
            {
                ErrorMessage = $"Provider '{CurrentLocation.Provider}' does not support enumeration.";
                _hasLoaded = true;
                RaiseViewStateChanged();
                return;
            }

            // 选中保持：刷新前记录选中项的路径，刷新后按 Path 匹配重新选中。Per ADR-0015 §6.
            var prevSelectedPaths = SelectedItems.Select(i => i.Path.Display).ToHashSet();

            var collected = new List<IItem>();
            await foreach (var item in container.GetChildrenAsync(
                CurrentLocation, new EnumerationOptions()).ConfigureAwait(false))
            {
                collected.Add(item);
            }

            // ObservableCollection 修改必须在 UI 线程。调用方在 UI 线程触发 Refresh，此处仍同步。
            // 刷新后立即应用当前排序（目录优先，再按 SortColumn 排序）
            collected = ApplySortToList(collected);
            // T-406: 保存完整列表（过滤前），再应用当前过滤
            _allItems = collected;
            _hasLoaded = true;
            var filtered = ApplyFilterToList(collected);
            Items.Clear();
            foreach (var item in filtered) Items.Add(item);

            // 恢复选中。
            SelectedItems.Clear();
            foreach (var item in filtered)
            {
                if (prevSelectedPaths.Contains(item.Path.Display))
                    SelectedItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _hasLoaded = true;
            RaiseViewStateChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>对当前 Items 集合重新排序（点击列头时调用）。</summary>
    private void ApplySort()
    {
        var sorted = ApplySortToList(_allItems);
        _allItems = sorted;
        var filtered = ApplyFilterToList(sorted);
        Items.Clear();
        foreach (var item in filtered) Items.Add(item);
    }

    /// <summary>T-406: 应用当前 FilterText 过滤。null/空字符串显示全部。</summary>
    private void ApplyFilter()
    {
        var filtered = ApplyFilterToList(_allItems);
        Items.Clear();
        foreach (var item in filtered) Items.Add(item);
        RaiseViewStateChanged();
    }

    private void RaiseViewStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasLoaded));
        this.RaisePropertyChanged(nameof(HasVisibleItems));
        this.RaisePropertyChanged(nameof(ShowLoadingState));
        this.RaisePropertyChanged(nameof(ShowEmptyState));
        this.RaisePropertyChanged(nameof(ShowFilterEmptyState));
        this.RaisePropertyChanged(nameof(ShowErrorState));
    }

    /// <summary>T-406: 对列表应用 FilterText 过滤。空过滤返回原列表。</summary>
    private List<IItem> ApplyFilterToList(List<IItem> source)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return source;
        var filter = _filterText.Trim();
        return source.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// 对列表排序：目录始终排在文件前面，同类型内按 SortColumn + SortDirection 排序。
    /// Explorer 行为：目录不参与文件排序规则，始终置顶。
    /// </summary>
    private List<IItem> ApplySortToList(List<IItem> source)
    {
        var isDir = new Func<IItem, bool>(i => i.Kind is ItemKind.Directory or ItemKind.Container);
        var dirs = source.Where(isDir).ToList();
        var files = source.Where(i => !isDir(i)).ToList();

        IEnumerable<IItem> sortedDirs = _sortColumn switch
        {
            SortColumn.Name => _sortDirection == SortDirection.Ascending
                ? dirs.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : dirs.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            SortColumn.Modified => _sortDirection == SortDirection.Ascending
                ? dirs.OrderBy(i => i.Timestamps.Modified ?? DateTimeOffset.MinValue)
                : dirs.OrderByDescending(i => i.Timestamps.Modified ?? DateTimeOffset.MinValue),
            _ => dirs.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };

        IEnumerable<IItem> sortedFiles = _sortColumn switch
        {
            SortColumn.Name => _sortDirection == SortDirection.Ascending
                ? files.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            SortColumn.Size => _sortDirection == SortDirection.Ascending
                ? files.OrderBy(i => i.Size ?? 0)
                : files.OrderByDescending(i => i.Size ?? 0),
            SortColumn.Type => _sortDirection == SortDirection.Ascending
                ? files.OrderBy(i => i.ContentType ?? "")
                : files.OrderByDescending(i => i.ContentType ?? ""),
            SortColumn.Modified => _sortDirection == SortDirection.Ascending
                ? files.OrderBy(i => i.Timestamps.Modified ?? DateTimeOffset.MinValue)
                : files.OrderByDescending(i => i.Timestamps.Modified ?? DateTimeOffset.MinValue),
            _ => files.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };

        return sortedDirs.Concat(sortedFiles).ToList();
    }
}

/// <summary>排序列枚举。</summary>
public enum SortColumn
{
    Name,
    Size,
    Type,
    Modified,
}

/// <summary>排序方向枚举。</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}
