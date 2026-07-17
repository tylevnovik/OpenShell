using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Operations;

/// <summary>
/// Default <see cref="IOperationEngine"/> implementation. Per ADR-0007.
/// Sealed per ADR-0007 constraints; extend via decorators.
/// </summary>
/// <remarks>
/// ADR-0044 §2 双轨制: <see cref="BeginCopy"/> 等 BeginXxx 方法需要 <see cref="ITaskCenter"/>
/// 来注册任务句柄。未注入 <see cref="ITaskCenter"/> 时, BeginXxx 抛 <see cref="InvalidOperationException"/>,
/// 旧 XxxAsync 方法不受影响 (向后兼容)。
/// </remarks>
public sealed class OperationEngine : IOperationEngine
{
    private readonly IProviderRegistry _providers;
    private readonly ITrashService? _trash;
    private readonly ITaskCenter? _taskCenter;

    public OperationEngine(
        IProviderRegistry providers,
        ITrashService? trash = null,
        ITaskCenter? taskCenter = null)
    {
        _providers = providers;
        _trash = trash;
        _taskCenter = taskCenter;
    }

    public async ValueTask<OperationResult> CopyAsync(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await CopyCoreAsync(source, destination, options, progress, cancellationToken, pause: null)
            .ConfigureAwait(false);

    /// <summary>
    /// 复制核心逻辑 (Per ADR-0044 §8): 支持可选暂停信号。
    /// 旧 <see cref="CopyAsync"/> 传 null (无暂停); <see cref="BeginCopy"/> 传 handle 的暂停信号。
    /// </summary>
    private async ValueTask<OperationResult> CopyCoreAsync(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        PauseSignal? pause)
    {
        options ??= new CopyOptions();
        var errors = new List<OperationError>();
        long bytes = 0;
        var items = 0;

        try
        {
            (bytes, items) = await CopyInternalAsync(source, destination, options, errors, progress, cancellationToken, pause)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled(items, bytes);
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("copy", ex.Message, ex);
        }

        if (errors.Count > 0)
            return OperationResult.Partial(items, bytes, errors);

        progress?.Report(new OperationProgress(bytes, null, "done", IsCompleted: true));
        return OperationResult.Successful(items, bytes);
    }

    private async Task<(long bytes, int items)> CopyInternalAsync(
        ItemPath source,
        ItemPath destination,
        CopyOptions options,
        List<OperationError> errors,
        IProgress<OperationProgress>? progress,
        CancellationToken ct,
        PauseSignal? pause = null)
    {
        var srcItemProvider = _providers.ResolveCapability<IItemProvider>(source)
            ?? throw new CapabilityNotSupported($"Provider '{source.Provider}' does not implement IItemProvider.");
        var dstWriter = _providers.ResolveCapability<IContentWriterProvider>(destination)
            ?? throw new CapabilityNotSupported($"Provider '{destination.Provider}' does not implement IContentWriterProvider.");

        var item = await srcItemProvider.GetItemAsync(source, ct).ConfigureAwait(false);
        if (item is null)
            throw new ItemNotFoundException($"Source not found: {source.Display}");

        long totalBytes = 0;
        int totalItems = 0;

        if (item.Kind is ItemKind.Directory or ItemKind.Container)
        {
            // For a directory, create it on the destination (if mutator available) and recurse.
            var dstMutator = _providers.ResolveCapability<IItemMutatorProvider>(destination);
            if (dstMutator is not null)
            {
                try { await dstMutator.CreateDirectoryAsync(destination, ct).ConfigureAwait(false); }
                catch { /* may already exist; ignore */ }
            }

            if (!options.Recurse)
                return (0, 0);

            var container = _providers.ResolveCapability<IContainerProvider>(source)
                ?? throw new CapabilityNotSupported($"Provider '{source.Provider}' does not implement IContainerProvider for recursion.");

            var enumOpts = new EnumerationOptions { Recurse = false };
            await foreach (var child in container.GetChildrenAsync(source, enumOpts, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                // 暂停检查点: 每处理一个子项前等待暂停信号释放。Per ADR-0044 §8.
                if (pause is not null) await pause.WaitAsync(ct).ConfigureAwait(false);
                var childDest = destination.Combine(child.Name);
                try
                {
                    var (b, n) = await CopyInternalAsync(child.Path, childDest, options, errors, progress, ct, pause)
                        .ConfigureAwait(false);
                    totalBytes += b;
                    totalItems += n;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add(new OperationError
                    {
                        Path = child.Path,
                        Phase = "copy-child",
                        Message = ex.Message,
                        Exception = ex,
                    });
                    if (options.StopOnError) return (totalBytes, totalItems);
                }
            }
            totalItems++;   // count the directory itself
        }
        else
        {
            // File: open read + open write + copy.
            var srcContent = _providers.ResolveCapability<IContentProvider>(source)
                ?? throw new CapabilityNotSupported($"Provider '{source.Provider}' does not implement IContentProvider.");

            if (!options.Force)
            {
                var dstItem = _providers.ResolveCapability<IItemProvider>(destination);
                if (dstItem is not null)
                {
                    var existing = await dstItem.GetItemAsync(destination, ct).ConfigureAwait(false);
                    if (existing is not null)
                        throw new ItemAlreadyExistsException($"Destination exists: {destination.Display}. Use -Force to overwrite.");
                }
            }

            await using var srcStream = await srcContent.OpenReadAsync(source, ct).ConfigureAwait(false);
            await using var dstStream = await dstWriter.OpenWriteAsync(destination, ct).ConfigureAwait(false);

            var buf = new byte[options.BufferSize];
            int read;
            long fileBytes = 0;
            while ((read = await srcStream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dstStream.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                fileBytes += read;
                progress?.Report(new OperationProgress(fileBytes, item.Size, $"copying {item.Name}", IsCompleted: false));
                // 暂停检查点: 每写入一个缓冲区后等待暂停信号释放。Per ADR-0044 §8.
                if (pause is not null) await pause.WaitAsync(ct).ConfigureAwait(false);
            }
            await dstStream.FlushAsync(ct).ConfigureAwait(false);
            totalBytes += fileBytes;
            totalItems++;
        }

        return (totalBytes, totalItems);
    }

    public async ValueTask<OperationResult> MoveAsync(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await MoveCoreAsync(source, destination, options, progress, cancellationToken, pause: null)
            .ConfigureAwait(false);

    /// <summary>
    /// 移动核心逻辑 (Per ADR-0044 §8): 支持可选暂停信号。
    /// 旧 <see cref="MoveAsync"/> 传 null (无暂停); <see cref="BeginMove"/> 传 handle 的暂停信号。
    /// </summary>
    private async ValueTask<OperationResult> MoveCoreAsync(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        PauseSignal? pause)
    {
        options ??= new MoveOptions();
        try
        {
            // Strategy: copy then delete. Same-provider providers may later expose a native move via mutator.
            var copyResult = await CopyCoreAsync(
                source, destination,
                new CopyOptions
                {
                    Recurse = true,
                    Force = options.Force,
                    StopOnError = options.StopOnError,
                    RollbackOnCancel = options.RollbackOnCancel,
                    BufferSize = options.BufferSize,
                },
                progress, cancellationToken, pause).ConfigureAwait(false);

            if (!copyResult.IsSuccess)
                return copyResult;

            // Delete source (physical, not trash — it's already been copied). 删除阶段不支持暂停。
            var deleteResult = await DeleteAsync(
                source,
                new DeleteOptions { Recurse = true, UseTrash = false, StopOnError = options.StopOnError },
                progress: null, cancellationToken).ConfigureAwait(false);

            if (!deleteResult.IsSuccess)
                return deleteResult;

            return OperationResult.Successful(
                copyResult.ItemsAffected,
                copyResult.BytesTransferred,
                new OperationJournalEntry
                {
                    Operation = "move-item",
                    Sources = new[] { source },
                    Destinations = new[] { destination },
                    ReverseOperation = "move-item",   // move back
                });
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("move", ex.Message, ex);
        }
    }

    public async ValueTask<OperationResult> DeleteAsync(
        ItemPath path,
        DeleteOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DeleteOptions();
        var errors = new List<OperationError>();
        int deleted = 0;

        try
        {
            var itemProvider = _providers.ResolveCapability<IItemProvider>(path)
                ?? throw new CapabilityNotSupported($"Provider '{path.Provider}' does not implement IItemProvider.");
            var item = await itemProvider.GetItemAsync(path, cancellationToken).ConfigureAwait(false);
            if (item is null)
                throw new ItemNotFoundException($"Not found: {path.Display}");

            // Trash path.
            if (options.UseTrash && _trash is not null)
            {
                var entry = await _trash.MoveToTrashAsync(path, cancellationToken).ConfigureAwait(false);
                deleted++;
                progress?.Report(new OperationProgress(deleted, null, $"trashed {item.Name}", IsCompleted: true));
                return OperationResult.Successful(deleted, 0,
                    new OperationJournalEntry
                    {
                        Operation = "remove-item",
                        Sources = new[] { path },
                        Destinations = new[] { entry.TrashPath },
                        ReverseOperation = "restore-item",
                        // 把 trashId 暴露给 JournalingOperationEngine, 用于构造 UndoInfo("restore-from-trash", {trashId=...})。
                        Parameters = new Dictionary<string, string>
                        {
                            ["trashId"] = entry.Id.ToString(),
                            ["trashPath"] = entry.TrashPath.Display,
                        },
                    });
            }

            // Physical delete via mutator.
            var mutator = _providers.ResolveCapability<IItemMutatorProvider>(path)
                ?? throw new CapabilityNotSupported($"Provider '{path.Provider}' does not implement IItemMutatorProvider; cannot delete.");

            if ((item.Kind is ItemKind.Directory or ItemKind.Container) && options.Recurse)
            {
                var container = _providers.ResolveCapability<IContainerProvider>(path);
                if (container is not null)
                {
                    var enumOpts = new EnumerationOptions { Recurse = false };
                    await foreach (var child in container.GetChildrenAsync(path, enumOpts, cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            await mutator.DeleteAsync(child.Path, recurse: true, cancellationToken).ConfigureAwait(false);
                            deleted++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            errors.Add(new OperationError { Path = child.Path, Phase = "delete-child", Message = ex.Message, Exception = ex });
                            if (options.StopOnError) return OperationResult.Partial(deleted, 0, errors);
                        }
                    }
                }
            }

            await mutator.DeleteAsync(path, recurse: options.Recurse, cancellationToken).ConfigureAwait(false);
            deleted++;
            progress?.Report(new OperationProgress(deleted, null, "done", IsCompleted: true));
            return OperationResult.Successful(deleted, 0);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled(deleted);
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("delete", ex.Message, ex);
        }
    }

    public async ValueTask<OperationResult> RenameAsync(
        ItemPath path,
        string newName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mutator = _providers.ResolveCapability<IItemMutatorProvider>(path)
                ?? throw new CapabilityNotSupported($"Provider '{path.Provider}' does not implement IItemMutatorProvider; cannot rename.");

            await mutator.RenameAsync(path, newName, cancellationToken).ConfigureAwait(false);

            var parent = path.GetParent();
            var newPath = parent.Combine(newName);
            return OperationResult.Successful(1, 0,
                new OperationJournalEntry
                {
                    Operation = "rename-item",
                    Sources = new[] { path },
                    Destinations = new[] { newPath },
                    ReverseOperation = "rename-item",
                });
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("rename", ex.Message, ex);
        }
    }

    public async ValueTask<OperationResult> TouchAsync(
        ItemPath path,
        TouchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TouchOptions();
        try
        {
            var itemProvider = _providers.ResolveCapability<IItemProvider>(path);
            var existing = itemProvider is not null
                ? await itemProvider.GetItemAsync(path, cancellationToken).ConfigureAwait(false)
                : null;

            if (existing is null && options.CreateIfMissing)
            {
                // Create empty file via content writer.
                var writer = _providers.ResolveCapability<IContentWriterProvider>(path)
                    ?? throw new CapabilityNotSupported($"Provider '{path.Provider}' does not implement IContentWriterProvider; cannot create.");
                await using var stream = await writer.OpenWriteAsync(path, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var mutator = _providers.ResolveCapability<IItemMutatorProvider>(path);
            if (mutator is not null)
            {
                var time = options.Time ?? DateTimeOffset.UtcNow;
                await mutator.SetTimestampsAsync(path, time, time, cancellationToken).ConfigureAwait(false);
            }

            return OperationResult.Successful(1, 0);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("touch", ex.Message, ex);
        }
    }

    public async ValueTask<OperationResult> CreateDirectoryAsync(
        ItemPath path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CreateDirectoryOptions();
        try
        {
            var mutator = _providers.ResolveCapability<IItemMutatorProvider>(path)
                ?? throw new CapabilityNotSupported($"Provider '{path.Provider}' does not implement IItemMutatorProvider; cannot create directory.");

            if (options.CreateIntermediate)
            {
                await CreateIntermediateAsync(path, mutator, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await mutator.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            }
            return OperationResult.Successful(1, 0);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("mkdir", ex.Message, ex);
        }
    }

    private async Task CreateIntermediateAsync(ItemPath path, IItemMutatorProvider mutator, CancellationToken ct)
    {
        // Walk up; create each missing ancestor first.
        var stack = new System.Collections.Generic.Stack<ItemPath>();
        var itemProvider = _providers.ResolveCapability<IItemProvider>(path);
        var current = path;
        while (current.InternalPath.Length > 0 && current.InternalPath != "/")
        {
            if (itemProvider is not null)
            {
                var existing = await itemProvider.GetItemAsync(current, ct).ConfigureAwait(false);
                if (existing is not null) break;
            }
            stack.Push(current);
            var parent = current.GetParent();
            if (parent.InternalPath == current.InternalPath) break;
            current = parent;
        }

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var p = stack.Pop();
            try { await mutator.CreateDirectoryAsync(p, ct).ConfigureAwait(false); }
            catch { /* already exists or other transient — ignore */ }
        }
    }

    // =========================================================================
    // ADR-0044 §2: BeginXxx 双轨制 —— 立即返回 ITaskHandle, 后台执行操作。
    // 句柄注册到 ITaskCenter (Per ADR-0044 §1), 调用方可订阅 ProgressChanged /
    // StateChanged, 并通过 CancelAsync / PauseAsync / ResumeAsync 控制生命周期。
    // 仅 Copy / Move 支持 Pause (Per ADR-0044 §8)。
    // =========================================================================

    /// <summary>启动复制任务并立即返回句柄。Per ADR-0044 §2.</summary>
    public ITaskHandle BeginCopy(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options = null,
        CancellationToken cancellationToken = default)
        => BeginOperation(
            operation: "copy",
            displayLabel: $"Copying {source.Display} to {destination.Display}",
            targetPath: destination.Display,
            supportsPause: true,
            cancellationToken,
            (pause, progress, ct) => CopyCoreAsync(source, destination, options, progress, ct, pause));

    /// <summary>启动移动任务并立即返回句柄。Per ADR-0044 §2.</summary>
    public ITaskHandle BeginMove(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options = null,
        CancellationToken cancellationToken = default)
        => BeginOperation(
            operation: "move",
            displayLabel: $"Moving {source.Display} to {destination.Display}",
            targetPath: destination.Display,
            supportsPause: true,
            cancellationToken,
            (pause, progress, ct) => MoveCoreAsync(source, destination, options, progress, ct, pause));

    /// <summary>启动删除任务并立即返回句柄。Per ADR-0044 §2.</summary>
    public ITaskHandle BeginDelete(
        ItemPath path,
        DeleteOptions? options = null,
        CancellationToken cancellationToken = default)
        => BeginOperation(
            operation: "delete",
            displayLabel: $"Deleting {path.Display}",
            targetPath: path.Display,
            supportsPause: false,
            cancellationToken,
            (_, progress, ct) => DeleteAsync(path, options, progress, ct));

    /// <summary>启动重命名任务并立即返回句柄。Per ADR-0044 §2.</summary>
    public ITaskHandle BeginRename(
        ItemPath path,
        string newName,
        CancellationToken cancellationToken = default)
        => BeginOperation(
            operation: "rename",
            displayLabel: $"Renaming {path.Display} to {newName}",
            targetPath: path.GetParent().Combine(newName).Display,
            supportsPause: false,
            cancellationToken,
            (_, progress, ct) => RenameAsync(path, newName, progress, ct));

    /// <summary>启动 touch 任务并立即返回句柄。Per ADR-0044 §2.</summary>
    public ITaskHandle BeginTouch(
        ItemPath path,
        TouchOptions? options = null,
        CancellationToken cancellationToken = default)
        => BeginOperation(
            operation: "touch",
            displayLabel: $"Touching {path.Display}",
            targetPath: path.Display,
            supportsPause: false,
            cancellationToken,
            (_, _, ct) => TouchAsync(path, options, ct));

    /// <summary>启动创建目录任务并立即返回句柄。Per ADR-0044 §2.</summary>
    public ITaskHandle BeginCreateDirectory(
        ItemPath path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default)
        => BeginOperation(
            operation: "mkdir",
            displayLabel: $"Creating directory {path.Display}",
            targetPath: path.Display,
            supportsPause: false,
            cancellationToken,
            (_, _, ct) => CreateDirectoryAsync(path, options, ct));

    /// <summary>
    /// BeginXxx 通用执行框架: 注册任务到 ITaskCenter, 后台 Task.Run 执行操作,
    /// 根据结果标记 Running / Completed / Failed / Cancelled。Per ADR-0044 §1 + §2.
    /// </summary>
    /// <param name="operation">操作类型名 ("copy", "move", ...)。</param>
    /// <param name="displayLabel">展示标签。</param>
    /// <param name="targetPath">目标路径 (用于 OperationCompletedEvent)。</param>
    /// <param name="supportsPause">是否支持暂停 (仅 copy/move)。</param>
    /// <param name="cancellationToken">调用方取消令牌 (链接到任务 CTS)。</param>
    /// <param name="run">
    /// 实际执行操作的回调。接收 (暂停信号, 进度适配器, 任务 CancellationToken)。
    /// 暂停信号在 copy/move 时非 null, 其他操作为 null。
    /// </param>
    /// <returns>立即返回的 ITaskHandle (任务可能尚未进入 Running 状态)。</returns>
    private ITaskHandle BeginOperation(
        string operation,
        string displayLabel,
        string? targetPath,
        bool supportsPause,
        CancellationToken cancellationToken,
        Func<PauseSignal?, IProgress<OperationProgress>, CancellationToken, ValueTask<OperationResult>> run)
    {
        if (_taskCenter is null)
        {
            throw new InvalidOperationException(
                "ITaskCenter not injected; BeginXxx requires task center for handle tracking. Per ADR-0044 §2.");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var registration = new TaskRegistration
        {
            Operation = operation,
            DisplayLabel = displayLabel,
            Cts = cts,
            SupportsPause = supportsPause,
            TargetPath = targetPath,
        };
        var handle = (TaskHandle)_taskCenter.Register(registration);
        var pause = supportsPause ? handle.PauseSignal : null;
        var progress = new TaskProgressAdapter(handle);

        // 后台执行: 标记 Running, 调用操作回调, 根据结果标记终态。
        // 不 await: BeginXxx 必须立即返回 handle 给调用方。
        _ = Task.Run(async () =>
        {
            handle.MarkRunning();
            try
            {
                var result = await run(pause, progress, cts.Token).ConfigureAwait(false);

                if (result.Status == OperationStatus.Cancelled) handle.MarkCancelled();
                else if (result.IsSuccess) handle.MarkCompleted();
                else handle.MarkFailed(new OperationException(result));
            }
            catch (OperationCanceledException)
            {
                handle.MarkCancelled();
            }
            catch (Exception ex)
            {
                handle.MarkFailed(ex);
            }
        }, cts.Token);

        return handle;
    }

    /// <summary>
    /// 把 ITaskHandle.ReportProgress 适配为 IProgress&lt;OperationProgress&gt;。
    /// 操作引擎内部 progress?.Report 调用会转发到 handle.ReportProgress (内含节流)。
    /// Per ADR-0044 §10.
    /// </summary>
    private sealed class TaskProgressAdapter : IProgress<OperationProgress>
    {
        private readonly TaskHandle _handle;
        public TaskProgressAdapter(TaskHandle handle) => _handle = handle;
        public void Report(OperationProgress value) => _handle.ReportProgress(value);
    }
}

/// <summary>Default options to avoid null allocations.</summary>
public static class OptionDefaults
{
    public static readonly CopyOptions Copy = new();
    public static readonly MoveOptions Move = new();
    public static readonly DeleteOptions Delete = new();
    public static readonly TouchOptions Touch = new();
    public static readonly CreateDirectoryOptions Mkdir = new();
}
