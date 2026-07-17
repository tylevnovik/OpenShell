using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.I18n;
using OpenShell.Operations;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// Modal progress dialog ViewModel for a single task. Per ADR-0044 §4.
/// Subscribes to ProgressChanged / StateChanged and raises INPC on the UI thread via
/// <see cref="RxApp.MainThreadScheduler"/>. Exposes Cancel / Background / Ok commands;
/// the dialog host listens to <see cref="RequestClose"/> to dismiss the modal.
/// </summary>
public sealed class ProgressDialogViewModel : ReactiveViewModel
{
    private readonly ITaskHandle _handle;
    private readonly II18nService? _i18n;

    /// <summary>
    /// Constructs the ProgressDialogViewModel wrapping the given handle.
    /// </summary>
    /// <param name="handle">The task handle to display. Must not be null.</param>
    public ProgressDialogViewModel(ITaskHandle handle, II18nService? i18n = null)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));

        // T-314: 从全局 DI 容器解析 II18nService (可选; 未注册时为 null, 回退硬编码英文)。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        // 进度变化：切到 UI 线程后 raise INPC。Per ADR-0044 §4.
        Observable.FromEventPattern<OperationProgress>(
            h => _handle.ProgressChanged += h,
            h => _handle.ProgressChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(LastProgress));
                this.RaisePropertyChanged(nameof(CurrentStatus));
                this.RaisePropertyChanged(nameof(Percent));
                this.RaisePropertyChanged(nameof(ProgressText));
                this.RaisePropertyChanged(nameof(IsIndeterminate));
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
                this.RaisePropertyChanged(nameof(ResultMessage));
            })
            .DisposeWith(Disposables);

        CancelCommand = ReactiveCommand.CreateFromTask(
            () => _handle.CancelAsync(),
            this.WhenAnyValue(x => x.CanCancel));
        CancelCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // BackgroundCommand：关闭对话框，任务继续后台运行。Per ADR-0044 §4.
        BackgroundCommand = ReactiveCommand.Create(() => RequestClose?.Invoke(this, EventArgs.Empty));
        BackgroundCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // OkCommand：仅在终态可用，关闭对话框。Per ADR-0044 §4.
        OkCommand = ReactiveCommand.Create(
            () => RequestClose?.Invoke(this, EventArgs.Empty),
            this.WhenAnyValue(x => x.IsCompleted));
        OkCommand.ThrownExceptions
            .Subscribe(_ => { })
            .DisposeWith(Disposables);

        // T-314: 订阅 LocaleChanged 事件，动态切换语言后刷新 ResultMessage 绑定 (随 Disposables 自动解订)。
        if (_i18n is not null)
        {
            Observable.FromEventPattern<EventHandler<string>, string>(
                    h => _i18n.LocaleChanged += h,
                    h => _i18n.LocaleChanged -= h)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ResultMessage)))
                .DisposeWith(Disposables);
        }
    }

    /// <summary>Dialog title (uses the task's display label).</summary>
    public string Title => _handle.DisplayLabel;

    /// <summary>The last progress snapshot.</summary>
    public OperationProgress? LastProgress => _handle.LastProgress;

    /// <summary>The current task state.</summary>
    public TaskState State => _handle.State;

    /// <summary>The current status string from progress, or empty string.</summary>
    public string CurrentStatus => _handle.LastProgress?.Status ?? string.Empty;

    /// <summary>Progress percentage 0..100. Returns 0 when indeterminate.</summary>
    public double Percent => ComputePercent(_handle.LastProgress);

    /// <summary>Formatted progress text such as "1.2 / 5.0 MB".</summary>
    public string ProgressText => FormatProgress(_handle.LastProgress);

    /// <summary>True when the task has no known total (animated bar should be shown).</summary>
    public bool IsIndeterminate => _handle.LastProgress?.Total.HasValue != true;

    /// <summary>True when the task reached a terminal state.</summary>
    public bool IsCompleted => _handle.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled;

    /// <summary>True if the task can still be cancelled.</summary>
    public bool CanCancel => !IsCompleted;

    /// <summary>Human-readable result message based on terminal state.</summary>
    public string ResultMessage => ComputeResultMessage();

    /// <summary>Command that cancels the underlying task.</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Command that requests the dialog to close (task continues in background).</summary>
    public ReactiveCommand<Unit, Unit> BackgroundCommand { get; }

    /// <summary>Command that requests the dialog to close after terminal state.</summary>
    public ReactiveCommand<Unit, Unit> OkCommand { get; }

    /// <summary>Raised when the dialog should close (Background or Ok clicked).</summary>
    public event EventHandler? RequestClose;

    private string ComputeResultMessage()
    {
        return _handle.State switch
        {
            TaskState.Completed => T("gui.progress.completed"),
            TaskState.Failed => T("gui.progress.failed", _handle.Exception?.Message ?? T("gui.progress.unknownError")),
            TaskState.Cancelled => T("gui.progress.cancelled"),
            _ => string.Empty,
        };
    }

    /// <summary>T-314: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

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
