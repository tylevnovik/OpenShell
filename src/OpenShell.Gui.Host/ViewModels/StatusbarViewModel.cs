using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// 状态栏 ViewModel。Per ADR-0013 §3 + ADR-0044 §5.
/// 显示当前路径、项数、选中大小、活动任务进度。
/// 订阅 <see cref="ITaskCenter.TaskAdded"/> / <see cref="ITaskCenter.TaskRemoved"/> 更新活动任务数。
/// </summary>
public sealed class StatusbarViewModel : ReactiveViewModel
{
    private readonly ITaskCenter _taskCenter;
    private readonly II18nService? _i18n;
    private ItemPath _currentLocation;
    private int _itemCount;
    private int _selectedCount;
    private int _activeTaskCount;
    // T-421: 选中项总大小（字节）
    private long _selectedSize;

    /// <summary>构造 StatusbarViewModel。</summary>
    /// <param name="taskCenter">任务中心，用于显示活动任务数。</param>
    public StatusbarViewModel(ITaskCenter taskCenter, II18nService? i18n = null)
    {
        _taskCenter = taskCenter ?? throw new ArgumentNullException(nameof(taskCenter));
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        // 订阅 locale 切换事件：语言变化时刷新 TasksLabel 文案。
        if (_i18n is not null)
        {
            _i18n.LocaleChanged += (_, _) =>
            {
                this.RaisePropertyChanged(nameof(TasksLabel));
                this.RaisePropertyChanged(nameof(SelectedSizeDisplay));
            };
        }

        // 订阅任务增删事件，刷新 ActiveTaskCount。Per ADR-0044 §5.
        // 事件可能在后台线程触发，订阅时切回 UI 线程由调用方处理（ReactiveCommand 默认主线程调度）。
        var added = Observable.FromEventPattern<ITaskHandle>(
            h => _taskCenter.TaskAdded += h,
            h => _taskCenter.TaskAdded -= h);
        var removed = Observable.FromEventPattern<ITaskHandle>(
            h => _taskCenter.TaskRemoved += h,
            h => _taskCenter.TaskRemoved -= h);

        added.Merge(removed)
            .Subscribe(_ => ActiveTaskCount = _taskCenter.ActiveTasks.Count)
            .DisposeWith(Disposables);

        // 初始化活动任务数。
        ActiveTaskCount = _taskCenter.ActiveTasks.Count;
    }

    /// <summary>当前路径（由 MainViewModel 推送，反映活动 Pane 位置）。Per ADR-0013 §3.</summary>
    public ItemPath CurrentLocation
    {
        get => _currentLocation;
        set => this.RaiseAndSetIfChanged(ref _currentLocation, value);
    }

    /// <summary>当前目录的项数。</summary>
    public int ItemCount
    {
        get => _itemCount;
        set => this.RaiseAndSetIfChanged(ref _itemCount, value);
    }

    /// <summary>当前选中的项数。</summary>
    public int SelectedCount
    {
        get => _selectedCount;
        set => this.RaiseAndSetIfChanged(ref _selectedCount, value);
    }

    /// <summary>活动任务数（> 0 时显示 "任务 N"）。Per ADR-0044 §5.</summary>
    public int ActiveTaskCount
    {
        get => _activeTaskCount;
        private set
        {
            if (_activeTaskCount == value) return;
            this.RaiseAndSetIfChanged(ref _activeTaskCount, value);
            this.RaisePropertyChanged(nameof(TasksLabel));
            this.RaisePropertyChanged(nameof(HasActiveTasks));
        }
    }

    /// <summary>T-421: 选中项总大小（字节）。</summary>
    public long SelectedSize
    {
        get => _selectedSize;
        private set => this.RaiseAndSetIfChanged(ref _selectedSize, value);
    }

    /// <summary>T-421: 选中项大小格式化文本（如 "12.3 MB"）。</summary>
    public string SelectedSizeLabel => FormatFileSize(_selectedSize);

    /// <summary>带本地化标签的选中大小文本。</summary>
    public string SelectedSizeDisplay => T("gui.status.selectedSize", SelectedSizeLabel);

    /// <summary>有选中项时才显示选中大小。</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>有后台任务时才显示任务入口。</summary>
    public bool HasActiveTasks => ActiveTaskCount > 0;

    /// <summary>状态栏任务标签文案（"Tasks: N"）。Per ADR-0044 §5.</summary>
    public string TasksLabel => T("gui.status.tasks", _activeTaskCount);

    /// <summary>i18n 翻译辅助：通过当前 II18nService 翻译 key 并格式化；服务不可用时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <summary>更新当前路径与项数统计。</summary>
    public void UpdateFromPane(PaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        CurrentLocation = pane.CurrentLocation;
        ItemCount = pane.Items.Count;
        SelectedCount = pane.SelectedItems.Count;
        // T-421: 计算选中项总大小
        SelectedSize = pane.SelectedItems.Sum(i => i.Size ?? 0);
        this.RaisePropertyChanged(nameof(SelectedSizeLabel));
        this.RaisePropertyChanged(nameof(SelectedSizeDisplay));
        this.RaisePropertyChanged(nameof(HasSelection));
    }

    /// <summary>T-421: 格式化文件大小为人类可读文本。</summary>
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
