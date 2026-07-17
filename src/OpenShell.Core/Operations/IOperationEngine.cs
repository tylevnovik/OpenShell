using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// Operation engine abstraction. Per ADR-0007.
/// All destructive commands (cp/mv/rm/rename/touch/mkdir) go through this engine so that:
/// 1. Cross-provider operations are handled uniformly (read from one, write to another).
/// 2. Batch progress and error aggregation have a single contract.
/// 3. Undo/Redo can wrap the engine via decorators (ADR-0020).
/// </summary>
/// <remarks>
/// 双轨制 API (Per ADR-0044 §2): 旧 <c>XxxAsync</c> 阻塞返回 <see cref="OperationResult"/>;
/// 新 <c>BeginXxx</c> 立即返回 <see cref="ITaskHandle"/> 用于任务追踪 (取消/暂停/后台),
/// 旧方法内部可调 <c>BeginXxx</c> 然后 await 句柄完成。新增 <c>BeginXxx</c> 不破坏 M1 既有签名。
/// </remarks>
public interface IOperationEngine
{
    ValueTask<OperationResult> CopyAsync(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<OperationResult> MoveAsync(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<OperationResult> DeleteAsync(
        ItemPath path,
        DeleteOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<OperationResult> RenameAsync(
        ItemPath path,
        string newName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<OperationResult> TouchAsync(
        ItemPath path,
        TouchOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<OperationResult> CreateDirectoryAsync(
        ItemPath path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default);

    // ----------------------------------------------------------------------
    // ADR-0044 §2: BeginXxx 双轨制 —— 返回任务句柄, 不阻塞调用方。
    // 句柄立即注册到 ITaskCenter (Per ADR-0044 §1), 调用方可订阅 ProgressChanged /
    // StateChanged, 并通过 CancelAsync / PauseAsync / ResumeAsync 控制生命周期。
    // ----------------------------------------------------------------------

    /// <summary>启动复制任务并立即返回句柄。Per ADR-0044 §2.</summary>
    ITaskHandle BeginCopy(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>启动移动任务并立即返回句柄。Per ADR-0044 §2.</summary>
    ITaskHandle BeginMove(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>启动删除任务并立即返回句柄。Per ADR-0044 §2.</summary>
    ITaskHandle BeginDelete(
        ItemPath path,
        DeleteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>启动重命名任务并立即返回句柄。Per ADR-0044 §2.</summary>
    ITaskHandle BeginRename(
        ItemPath path,
        string newName,
        CancellationToken cancellationToken = default);

    /// <summary>启动 touch 任务并立即返回句柄。Per ADR-0044 §2.</summary>
    ITaskHandle BeginTouch(
        ItemPath path,
        TouchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>启动创建目录任务并立即返回句柄。Per ADR-0044 §2.</summary>
    ITaskHandle BeginCreateDirectory(
        ItemPath path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default);
}
