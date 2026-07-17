using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// <see cref="IOperationEngine"/> 装饰器: 在每次操作前后跟踪 in-flight 计数。Per ADR-0016 §3 第 2 步。
/// 包装内层引擎, 解析 <see cref="ItemPath.Provider"/> 段, 调用 <see cref="IOperationTracker.Increment"/> / <see cref="IOperationTracker.Decrement"/>。
/// 这样 <c>IPluginLoader.UnloadAsync</c> 在卸载前可通过 tracker 等待所有 in-flight 操作完成, 防止卸载 ALC 时仍有代码在执行。
/// 装饰器顺序: JournalingOperationEngine(TrackingOperationEngine(OperationEngine))。
/// </summary>
public sealed class TrackingOperationEngine : IOperationEngine
{
    private readonly IOperationEngine _inner;
    private readonly IOperationTracker _tracker;

    public TrackingOperationEngine(IOperationEngine inner, IOperationTracker tracker)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    /// <inheritdoc />
    public ValueTask<OperationResult> CopyAsync(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => TrackAsync(
            new[] { source.Provider, destination.Provider },
            () => _inner.CopyAsync(source, destination, options, progress, cancellationToken));

    /// <inheritdoc />
    public ValueTask<OperationResult> MoveAsync(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => TrackAsync(
            new[] { source.Provider, destination.Provider },
            () => _inner.MoveAsync(source, destination, options, progress, cancellationToken));

    /// <inheritdoc />
    public ValueTask<OperationResult> DeleteAsync(
        ItemPath path,
        DeleteOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => TrackAsync(
            new[] { path.Provider },
            () => _inner.DeleteAsync(path, options, progress, cancellationToken));

    /// <inheritdoc />
    public ValueTask<OperationResult> RenameAsync(
        ItemPath path,
        string newName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => TrackAsync(
            new[] { path.Provider },
            () => _inner.RenameAsync(path, newName, progress, cancellationToken));

    /// <inheritdoc />
    public ValueTask<OperationResult> TouchAsync(
        ItemPath path,
        TouchOptions? options = null,
        CancellationToken cancellationToken = default)
        => TrackAsync(
            new[] { path.Provider },
            () => _inner.TouchAsync(path, options, cancellationToken));

    /// <inheritdoc />
    public ValueTask<OperationResult> CreateDirectoryAsync(
        ItemPath path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default)
        => TrackAsync(
            new[] { path.Provider },
            () => _inner.CreateDirectoryAsync(path, options, cancellationToken));

    // ----------------------------------------------------------------------
    // ADR-0044 §2: BeginXxx 委托内层引擎, 通过 StateChanged 订阅实现 in-flight 跟踪。
    // ----------------------------------------------------------------------

    /// <inheritdoc />
    public ITaskHandle BeginCopy(
        ItemPath source, ItemPath destination, CopyOptions? options = null, CancellationToken cancellationToken = default)
        => TrackBegin(_inner.BeginCopy(source, destination, options, cancellationToken),
            new[] { source.Provider, destination.Provider });

    /// <inheritdoc />
    public ITaskHandle BeginMove(
        ItemPath source, ItemPath destination, MoveOptions? options = null, CancellationToken cancellationToken = default)
        => TrackBegin(_inner.BeginMove(source, destination, options, cancellationToken),
            new[] { source.Provider, destination.Provider });

    /// <inheritdoc />
    public ITaskHandle BeginDelete(
        ItemPath path, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => TrackBegin(_inner.BeginDelete(path, options, cancellationToken),
            new[] { path.Provider });

    /// <inheritdoc />
    public ITaskHandle BeginRename(
        ItemPath path, string newName, CancellationToken cancellationToken = default)
        => TrackBegin(_inner.BeginRename(path, newName, cancellationToken),
            new[] { path.Provider });

    /// <inheritdoc />
    public ITaskHandle BeginTouch(
        ItemPath path, TouchOptions? options = null, CancellationToken cancellationToken = default)
        => TrackBegin(_inner.BeginTouch(path, options, cancellationToken),
            new[] { path.Provider });

    /// <inheritdoc />
    public ITaskHandle BeginCreateDirectory(
        ItemPath path, CreateDirectoryOptions? options = null, CancellationToken cancellationToken = default)
        => TrackBegin(_inner.BeginCreateDirectory(path, options, cancellationToken),
            new[] { path.Provider });

    /// <summary>
    /// 为 BeginXxx 返回的句柄附加 in-flight 跟踪: 任务进入 Running 时 Increment,
    /// 进入终态时 Decrement。Per ADR-0016 §3: 确保 UnloadAsync 等待后台操作完成。
    /// </summary>
    private ITaskHandle TrackBegin(ITaskHandle handle, string[] providerNames)
    {
        var distinct = DistinctProviders(providerNames);
        bool incremented = false;

        handle.StateChanged += (_, state) =>
        {
            if (state == TaskState.Running && !incremented)
            {
                foreach (var name in distinct) _tracker.Increment(name);
                incremented = true;
            }
            else if (state is TaskState.Completed or TaskState.Failed or TaskState.Cancelled && incremented)
            {
                foreach (var name in distinct) _tracker.Decrement(name);
                incremented = false;
            }
        };

        // 若句柄已经处于 Running (同步触发), 补一次 Increment。
        if (handle.State == TaskState.Running && !incremented)
        {
            foreach (var name in distinct) _tracker.Increment(name);
            incremented = true;
        }

        return handle;
    }

    /// <summary>
    /// 通用跟踪包装: 对涉及的全部 provider (去重) Increment, 执行操作, 无论结果如何 (含异常/取消) 都 Decrement。
    /// </summary>
    private async ValueTask<OperationResult> TrackAsync(string[] providerNames, Func<ValueTask<OperationResult>> action)
    {
        // 去重 (大小写不敏感): 跨 provider 操作 (如 fs → sftp) 需对两端分别计数, 同 provider 操作只计一次。
        var distinct = DistinctProviders(providerNames);
        foreach (var name in distinct)
        {
            _tracker.Increment(name);
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            // 必须在 finally 中 Decrement, 保证异常/取消路径也释放计数。
            foreach (var name in distinct)
            {
                _tracker.Decrement(name);
            }
        }
    }

    private static List<string> DistinctProviders(string[] names)
    {
        if (names.Length == 0) return new List<string>(0);
        if (names.Length == 1) return new List<string> { names[0] };
        var result = new List<string>(names.Length);
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n)) continue;
            var found = false;
            foreach (var r in result)
            {
                if (string.Equals(r, n, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found) result.Add(n);
        }
        return result;
    }
}
