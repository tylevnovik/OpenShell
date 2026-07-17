using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using OpenShell;
using OpenShell.Operations;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// Wraps a single <see cref="ITaskHandle"/> for UI binding. Per ADR-0044 §3.
/// Subscribes to ProgressChanged / StateChanged and raises INPC on the UI thread
/// via <see cref="RxApp.MainThreadScheduler"/> (test-friendly; does not reference Avalonia).
/// </summary>
public sealed class TaskItemViewModel : ReactiveViewModel
{
    private readonly ITaskHandle _handle;
    private bool _isForeground;

    /// <summary>
    /// Constructs a TaskItemViewModel wrapping the given handle.
    /// </summary>
    /// <param name="handle">The task handle to wrap. Must not be null.</param>
    public TaskItemViewModel(ITaskHandle handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));

        // 进度变化：切到 UI 线程后 raise INPC。Per ADR-0044 §3.
        Observable.FromEventPattern<OperationProgress>(
            h => _handle.ProgressChanged += h,
            h => _handle.ProgressChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(LastProgress));
                this.RaisePropertyChanged(nameof(ProgressPercent));
                this.RaisePropertyChanged(nameof(ProgressText));
            })
            .DisposeWith(Disposables);

        // 状态变化：切到 UI 线程后 raise INPC（包含派生属性）。
        Observable.FromEventPattern<TaskState>(
            h => _handle.StateChanged += h,
            h => _handle.StateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(State));
                this.RaisePropertyChanged(nameof(IsCompleted));
                this.RaisePropertyChanged(nameof(CanCancel));
                this.RaisePropertyChanged(nameof(CanPause));
                this.RaisePropertyChanged(nameof(CanResume));
                this.RaisePropertyChanged(nameof(CanBackground));
                this.RaisePropertyChanged(nameof(CanRestore));
                this.RaisePropertyChanged(nameof(CompletedAt));
                this.RaisePropertyChanged(nameof(Exception));
                this.RaisePropertyChanged(nameof(ProgressPercent));
                this.RaisePropertyChanged(nameof(ProgressText));
            })
            .DisposeWith(Disposables);

        // CancelCommand：基于 CanCancel 可执行性。Per ADR-0013 约束订阅 ThrownExceptions。
        CancelCommand = ReactiveCommand.CreateFromTask(
            () => _handle.CancelAsync(),
            this.WhenAnyValue(x => x.CanCancel));
        CancelCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // PauseCommand：仅 SupportsPause 且 Running 时可用。Per ADR-0044 §8.
        PauseCommand = ReactiveCommand.CreateFromTask(
            () => _handle.PauseAsync(),
            this.WhenAnyValue(x => x.CanPause));
        PauseCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // ResumeCommand：仅 SupportsPause 且 Paused 时可用。Per ADR-0044 §8.
        ResumeCommand = ReactiveCommand.CreateFromTask(
            () => _handle.ResumeAsync(),
            this.WhenAnyValue(x => x.CanResume));
        ResumeCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // BackgroundCommand：把前台进度对话框中的任务发送到任务中心后台。Per ADR-0044 §3.
        // 仅当任务在前台显示 (IsForeground) 且未完成时可用。
        BackgroundCommand = ReactiveCommand.Create(
            () => { IsForeground = false; },
            this.WhenAnyValue(x => x.IsForeground, x => x.CanCancel)
                .Select(t => t.Item1 && t.Item2));
        BackgroundCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // RestoreCommand：把任务中心后台任务恢复到前台进度对话框。Per ADR-0044 §3.
        // 仅当任务不在前台显示且未完成时可用。
        RestoreCommand = ReactiveCommand.Create(
            () => { IsForeground = true; },
            this.WhenAnyValue(x => x.IsForeground, x => x.CanCancel)
                .Select(t => !t.Item1 && t.Item2));
        RestoreCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);
    }

    /// <summary>The task's unique id.</summary>
    public Guid TaskId => _handle.TaskId;

    /// <summary>The display label (e.g. "Copying 3 items to fs::D:/Backup").</summary>
    public string DisplayLabel => _handle.DisplayLabel;

    /// <summary>The operation type name (e.g. "copy", "move").</summary>
    public string Operation => _handle.Operation;

    /// <summary>The current task state.</summary>
    public TaskState State => _handle.State;

    /// <summary>The last reported progress snapshot, or null if not yet started.</summary>
    public OperationProgress? LastProgress => _handle.LastProgress;

    /// <summary>The exception if the task failed, otherwise null.</summary>
    public Exception? Exception => _handle.Exception;

    /// <summary>The task start time.</summary>
    public DateTimeOffset StartedAt => _handle.StartedAt;

    /// <summary>The task completion time, or null if not yet completed.</summary>
    public DateTimeOffset? CompletedAt => _handle.CompletedAt;

    /// <summary>
    /// Progress percentage 0..100. Returns 0 when total is unknown (indeterminate).
    /// </summary>
    public double ProgressPercent => ComputePercent(_handle.LastProgress);

    /// <summary>
    /// Formatted progress text such as "1.2 / 5.0 MB". Returns empty string if no progress yet.
    /// </summary>
    public string ProgressText => FormatProgress(_handle.LastProgress);

    /// <summary>True if the task has reached a terminal state (Completed, Failed or Cancelled).</summary>
    public bool IsCompleted => _handle.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled;

    /// <summary>True if the task can still be cancelled.</summary>
    public bool CanCancel => !IsCompleted;

    /// <summary>True if the task supports pause and is currently Running. Per ADR-0044 §8.</summary>
    public bool CanPause => _handle.SupportsPause && _handle.State == TaskState.Running;

    /// <summary>True if the task supports pause and is currently Paused. Per ADR-0044 §8.</summary>
    public bool CanResume => _handle.SupportsPause && _handle.State == TaskState.Paused;

    /// <summary>True if the Background command can fire (task is in foreground dialog and still active). Per ADR-0044 §3.</summary>
    public bool CanBackground => IsForeground && CanCancel;

    /// <summary>True if the Restore command can fire (task is in task center and still active). Per ADR-0044 §3.</summary>
    public bool CanRestore => !IsForeground && CanCancel;

    /// <summary>
    /// 是否在前台进度对话框中显示 (vs 后台任务中心面板)。Per ADR-0044 §3.
    /// BackgroundCommand 置 false, RestoreCommand 置 true。
    /// </summary>
    public bool IsForeground
    {
        get => _isForeground;
        set
        {
            this.RaiseAndSetIfChanged(ref _isForeground, value);
            this.RaisePropertyChanged(nameof(CanBackground));
            this.RaisePropertyChanged(nameof(CanRestore));
        }
    }

    /// <summary>Command that cancels the underlying task. Disabled when terminal.</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Command that pauses the task (only for copy/move). Per ADR-0044 §8.</summary>
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }

    /// <summary>Command that resumes a paused task. Per ADR-0044 §8.</summary>
    public ReactiveCommand<Unit, Unit> ResumeCommand { get; }

    /// <summary>Command that sends the task from foreground dialog to task center. Per ADR-0044 §3.</summary>
    public ReactiveCommand<Unit, Unit> BackgroundCommand { get; }

    /// <summary>Command that restores the task from task center to foreground dialog. Per ADR-0044 §3.</summary>
    public ReactiveCommand<Unit, Unit> RestoreCommand { get; }

    private static double ComputePercent(OperationProgress? progress)
    {
        if (progress is not { } p) return 0;
        if (p.Total is { } total && total > 0)
        {
            var pct = (double)p.Completed / total * 100.0;
            return Math.Clamp(pct, 0, 100);
        }
        return 0;
    }

    private static string FormatProgress(OperationProgress? progress)
    {
        if (progress is not { } p) return string.Empty;
        if (p.Total is { } total)
        {
            return $"{FormatBytes(p.Completed)} / {FormatBytes(total)}";
        }
        if (!string.IsNullOrEmpty(p.Status)) return p.Status!;
        return FormatBytes(p.Completed);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };
    }
}
