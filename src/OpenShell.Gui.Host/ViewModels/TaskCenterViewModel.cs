using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using OpenShell.Operations;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// The Task Center panel ViewModel. Per ADR-0044 §3.
/// Mirrors <see cref="ITaskCenter.ActiveTasks"/> and <see cref="ITaskCenter.RecentCompleted"/>
/// into observable collections, marshaling event delivery to the UI thread via
/// <see cref="RxApp.MainThreadScheduler"/> (no Avalonia reference, test-friendly).
/// </summary>
public sealed class TaskCenterViewModel : ReactiveViewModel
{
    /// <summary>最近完成列表的最大容量。Per ADR-0044 §1（默认 50 条 FIFO 丢弃）。</summary>
    private const int MaxCompleted = 50;

    private readonly ITaskCenter _center;

    /// <summary>
    /// Constructs the TaskCenterViewModel from the given task center.
    /// Pre-populates from existing ActiveTasks and RecentCompleted snapshots so
    /// the panel reflects tasks already registered before the view was created.
    /// </summary>
    /// <param name="center">The task center to mirror. Must not be null.</param>
    public TaskCenterViewModel(ITaskCenter center)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));

        Active = new ObservableCollection<TaskItemViewModel>();
        Completed = new ObservableCollection<TaskItemViewModel>();

        // 预填充已有活动任务。Per ADR-0044 §3.
        foreach (var h in _center.ActiveTasks)
        {
            Active.Add(new TaskItemViewModel(h));
        }

        // RecentCompleted 内部按 FIFO 入队，这里反转为最近在前。
        foreach (var h in _center.RecentCompleted)
        {
            Completed.Insert(0, new TaskItemViewModel(h));
        }
        TrimCompleted();

        // 订阅 TaskAdded：在 UI 线程把新任务加入 Active。
        Observable.FromEventPattern<ITaskHandle>(
            h => _center.TaskAdded += h,
            h => _center.TaskAdded -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ep => Active.Add(new TaskItemViewModel(ep.EventArgs)))
            .DisposeWith(Disposables);

        // 订阅 TaskRemoved：从 Active 移除并插入 Completed 头部。
        Observable.FromEventPattern<ITaskHandle>(
            h => _center.TaskRemoved += h,
            h => _center.TaskRemoved -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ep => OnTaskRemoved(ep.EventArgs))
            .DisposeWith(Disposables);

        // Active 变化时刷新派生属性。
        Active.CollectionChanged += OnActiveCollectionChanged;

        // ClearCompletedCommand：清空 Completed 并释放 VM 订阅。
        ClearCompletedCommand = ReactiveCommand.Create(ClearCompletedCore);
        ClearCompletedCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);
    }

    /// <summary>Active task list (Pending, Running, Paused). Bind to ItemsControl.ItemsSource.</summary>
    public ObservableCollection<TaskItemViewModel> Active { get; }

    /// <summary>Recently completed task list (max 50, most-recent first).</summary>
    public ObservableCollection<TaskItemViewModel> Completed { get; }

    /// <summary>Number of active tasks. Computed from Active.Count.</summary>
    public int ActiveCount => Active.Count;

    /// <summary>True if there are any active tasks.</summary>
    public bool HasActiveTasks => Active.Count > 0;

    /// <summary>Command that clears the Completed list and disposes its VMs.</summary>
    public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Active.CollectionChanged -= OnActiveCollectionChanged;
            foreach (var item in Active) item.Dispose();
            foreach (var item in Completed) item.Dispose();
            Active.Clear();
            Completed.Clear();
        }
        base.Dispose(disposing);
    }

    private void OnTaskRemoved(ITaskHandle handle)
    {
        for (int i = 0; i < Active.Count; i++)
        {
            if (Active[i].TaskId == handle.TaskId)
            {
                Active[i].Dispose();
                Active.RemoveAt(i);
                break;
            }
        }
        Completed.Insert(0, new TaskItemViewModel(handle));
        TrimCompleted();
    }

    private void TrimCompleted()
    {
        while (Completed.Count > MaxCompleted)
        {
            var last = Completed[Completed.Count - 1];
            Completed.RemoveAt(Completed.Count - 1);
            last.Dispose();
        }
    }

    private void ClearCompletedCore()
    {
        foreach (var item in Completed) item.Dispose();
        Completed.Clear();
    }

    private void OnActiveCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ActiveCount));
        this.RaisePropertyChanged(nameof(HasActiveTasks));
    }
}
