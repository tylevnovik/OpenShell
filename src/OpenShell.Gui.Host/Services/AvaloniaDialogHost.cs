using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using OpenShell.Gui.Abstractions;

namespace OpenShell.Gui.Host.Services;

/// <summary>
/// Avalonia 实现 <see cref="IDialogHost"/>。Per ADR-0043 §2, §3.
/// 把任意 view (实际是 Avalonia <see cref="Window"/>) 作为模态子窗口显示，
/// owner 为应用主窗口。供 <see cref="IDialogView{T}"/> 实现委托调用。
/// </summary>
internal sealed class AvaloniaDialogHost : IDialogHost
{
    /// <summary>
    /// 懒解析 MainWindow：DI 容器在 Avalonia Application 启动前就构建，
    /// 此时 MainWindow 尚未创建。所有 ShowAsync 调用时 MainWindow 已就绪。
    /// </summary>
    private static Window MainWindow
    {
        get
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
                return window;
            }

            throw new InvalidOperationException(
                "AvaloniaDialogHost 要求已运行的 IClassicDesktopStyleApplicationLifetime 且 MainWindow 已创建。");
        }
    }

    /// <inheritdoc />
    public async Task<TResult> ShowAsync<TResult>(object view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        ct.ThrowIfCancellationRequested();

        if (view is not Window window)
        {
            throw new ArgumentException(
                $"AvaloniaDialogHost 要求 view 是 Avalonia Window, 实际收到 {view.GetType().FullName}.",
                nameof(view));
        }

        // 用泛型 ShowDialog<TResult> 触发 Avalonia 模态循环并返回结果。
        // IDialogView<T> 的实现类型一般是带结果字段的 Window, 关闭前设置 DialogResult。
        ct.ThrowIfCancellationRequested();
        return await window.ShowDialog<TResult>(MainWindow);
    }
}
