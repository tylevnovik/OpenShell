using System.Reactive.Disposables;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// ViewModel 基类。Per ADR-0013 §2.
/// 提供 <see cref="CompositeDisposable"/> 集中管理订阅生命周期，避免内存泄漏。
/// ViewModel 不引用 Avalonia.* 命名空间，可在 .NET 控制台测试项目跑单测。
/// </summary>
public abstract class ReactiveViewModel : ReactiveObject, IDisposable
{
    /// <summary>本 ViewModel 所有订阅的 disposable 集合。子类用 <c>Disposables.Add(...)</c> 注册。</summary>
    protected readonly CompositeDisposable Disposables = new();

    private int _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>子类重写以释放非托管 / 订阅资源。</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) Disposables.Dispose();
    }
}
