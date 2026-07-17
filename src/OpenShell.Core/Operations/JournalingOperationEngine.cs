using OpenShell.Paths;

namespace OpenShell.Operations;

/// <summary>
/// 把 <see cref="IOperationEngine"/> 的操作包装为带 journal 的装饰器。Per ADR-0007 §装饰器链 + ADR-0020 §6.
/// 每次成功操作后追加 <see cref="OperationJournalEntry"/> 到 <see cref="IOperationJournal"/>,
/// 同时构造 <see cref="UndoInfo"/> 用于 Undo 反向执行。
/// 必须是装饰器链最外层 (最先记录, 最后执行 undo) — ADR-0020 §10 约束。
/// </summary>
public sealed class JournalingOperationEngine : IOperationEngine
{
    private readonly IOperationEngine _inner;
    private readonly IOperationJournal _journal;

    /// <summary>
    /// Async-local 抑制标志: Undo/Redo 期间执行的反向/正向操作不应再次被 journal。
    /// 通过 <see cref="BeginSuppress"/> / <see cref="EndSuppress"/> 管理。
    /// </summary>
    private static readonly AsyncLocal<bool> _suppressJournaling = new();

    /// <summary>构造 JournalingOperationEngine。</summary>
    /// <param name="inner">内层操作引擎 (通常是 <see cref="OperationEngine"/>)。</param>
    /// <param name="journal">操作日志, 用于持久化 Undo/Redo 记录。</param>
    public JournalingOperationEngine(IOperationEngine inner, IOperationJournal journal)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    /// <summary>开始抑制 journaling (Undo/Redo 期间调用, 防止内部反向操作被重复记录)。</summary>
    public static void BeginSuppress() => _suppressJournaling.Value = true;

    /// <summary>结束抑制 journaling。</summary>
    public static void EndSuppress() => _suppressJournaling.Value = false;

    /// <summary>当前是否处于抑制状态。</summary>
    public static bool IsSuppressed => _suppressJournaling.Value;

    /// <inheritdoc />
    public async ValueTask<OperationResult> CopyAsync(
        ItemPath source,
        ItemPath destination,
        CopyOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.CopyAsync(source, destination, options, progress, cancellationToken).ConfigureAwait(false);

        // Undo/Redo 期间执行的正向操作不应再次被 journal (ADR-0020 §7), 直接返回。
        if (IsSuppressed) return result;

        if (result.IsSuccess)
        {
            // Copy → Undo: delete destination (复制产生的副本)。Per ADR-0020 §3.
            var entry = new OperationJournalEntry
            {
                Operation = "copy",
                Sources = new[] { source },
                Destinations = new[] { destination },
                ReverseOperation = "delete",
                Parameters = BuildParameters(options),
                Undo = new UndoInfo("delete", new Dictionary<string, string>
                {
                    ["path"] = destination.Display,
                }),
            };
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult> MoveAsync(
        ItemPath source,
        ItemPath destination,
        MoveOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.MoveAsync(source, destination, options, progress, cancellationToken).ConfigureAwait(false);

        // Undo/Redo 期间执行的正向操作不应再次被 journal (ADR-0020 §7), 直接返回。
        if (IsSuppressed) return result;

        if (result.IsSuccess)
        {
            // Move → Undo: move destination back to source (把文件移回原位置)。Per ADR-0020 §3.
            var entry = new OperationJournalEntry
            {
                Operation = "move",
                Sources = new[] { source },
                Destinations = new[] { destination },
                ReverseOperation = "move-back",
                Parameters = BuildParameters(options),
                Undo = new UndoInfo("move-back", new Dictionary<string, string>
                {
                    ["src"] = destination.Display,  // 当前位置 (forward move 的 destination)
                    ["dst"] = source.Display,        // 原位置 (forward move 的 source)
                }),
            };
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult> DeleteAsync(
        ItemPath path,
        DeleteOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DeleteOptions();
        var result = await _inner.DeleteAsync(path, options, progress, cancellationToken).ConfigureAwait(false);

        // Undo/Redo 期间执行的正向操作不应再次被 journal (ADR-0020 §7), 直接返回。
        if (IsSuppressed) return result;

        if (result.IsSuccess)
        {
            UndoInfo? undo;
            if (options.UseTrash)
            {
                // Delete (走 trash) → Undo: restore-from-trash。trashId 从内层 result.JournalEntry.Parameters 读取。
                string? trashId = null;
                if (result.JournalEntry?.Parameters.TryGetValue("trashId", out var id) == true)
                {
                    trashId = id;
                }
                undo = trashId is not null
                    ? new UndoInfo("restore-from-trash", new Dictionary<string, string>
                    {
                        ["trashId"] = trashId,
                    })
                    : null;  // 找不到 trashId 不可逆 (理论上不应发生)
            }
            else
            {
                // Delete (force, 物理删除) → 不可逆。Per ADR-0020 §3.
                undo = null;
            }

            var entry = new OperationJournalEntry
            {
                Operation = "delete",
                Sources = new[] { path },
                Destinations = result.JournalEntry?.Destinations ?? Array.Empty<ItemPath>(),
                ReverseOperation = options.UseTrash ? "restore-from-trash" : string.Empty,
                Parameters = new Dictionary<string, string>
                {
                    ["useTrash"] = options.UseTrash.ToString(),
                    ["recurse"] = options.Recurse.ToString(),
                },
                Undo = undo,
            };
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult> RenameAsync(
        ItemPath path,
        string newName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.RenameAsync(path, newName, progress, cancellationToken).ConfigureAwait(false);

        // Undo/Redo 期间执行的正向操作不应再次被 journal (ADR-0020 §7), 直接返回。
        if (IsSuppressed) return result;

        if (result.IsSuccess)
        {
            // Rename → Undo: rename newPath back to oldName。Per ADR-0020 §3.
            var parent = path.GetParent();
            var newPath = parent.Combine(newName);
            var oldName = path.GetName();

            var entry = new OperationJournalEntry
            {
                Operation = "rename",
                Sources = new[] { path },
                Destinations = new[] { newPath },
                ReverseOperation = "rename",
                Parameters = new Dictionary<string, string>
                {
                    ["newName"] = newName,
                    ["oldName"] = oldName,
                },
                Undo = new UndoInfo("rename", new Dictionary<string, string>
                {
                    ["path"] = newPath.Display,    // 当前路径 (rename 后)
                    ["newName"] = oldName,          // 改回原名
                }),
            };
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult> TouchAsync(
        ItemPath path,
        TouchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.TouchAsync(path, options, cancellationToken).ConfigureAwait(false);

        // Undo/Redo 期间执行的正向操作不应再次被 journal (ADR-0020 §7), 直接返回。
        if (IsSuppressed) return result;

        if (result.IsSuccess)
        {
            // Touch → Undo: delete touchedPath (仅当 touch 创建了新文件时才可逆, 这里统一记录, undo 时若不存在则忽略)。
            // Per ADR-0020 §3: New-Item (文件) → Delete File.
            var entry = new OperationJournalEntry
            {
                Operation = "touch",
                Sources = new[] { path },
                Destinations = Array.Empty<ItemPath>(),
                ReverseOperation = "delete",
                Parameters = BuildParameters(options),
                Undo = new UndoInfo("delete", new Dictionary<string, string>
                {
                    ["path"] = path.Display,
                }),
            };
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult> CreateDirectoryAsync(
        ItemPath path,
        CreateDirectoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.CreateDirectoryAsync(path, options, cancellationToken).ConfigureAwait(false);

        // Undo/Redo 期间执行的正向操作不应再次被 journal (ADR-0020 §7), 直接返回。
        if (IsSuppressed) return result;

        if (result.IsSuccess)
        {
            // CreateDirectory → Undo: delete createdPath (删除新建的空目录; 非空报错)。Per ADR-0020 §3.
            var entry = new OperationJournalEntry
            {
                Operation = "mkdir",
                Sources = new[] { path },
                Destinations = Array.Empty<ItemPath>(),
                ReverseOperation = "delete",
                Parameters = BuildParameters(options),
                Undo = new UndoInfo("delete", new Dictionary<string, string>
                {
                    ["path"] = path.Display,
                }),
            };
            await _journal.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    // ----------------------------------------------------------------------
    // ADR-0044 §2: BeginXxx 委托内层引擎, 不附加 journaling。
    // 后台异步操作的 journaling 由调用方在完成后自行决定 (或通过 OperationCompletedEvent
    // 订阅追加), 此处保持透传以支持取消/暂停句柄的完整生命周期。
    // ----------------------------------------------------------------------

    /// <inheritdoc />
    public ITaskHandle BeginCopy(
        ItemPath source, ItemPath destination, CopyOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.BeginCopy(source, destination, options, cancellationToken);

    /// <inheritdoc />
    public ITaskHandle BeginMove(
        ItemPath source, ItemPath destination, MoveOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.BeginMove(source, destination, options, cancellationToken);

    /// <inheritdoc />
    public ITaskHandle BeginDelete(
        ItemPath path, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.BeginDelete(path, options, cancellationToken);

    /// <inheritdoc />
    public ITaskHandle BeginRename(
        ItemPath path, string newName, CancellationToken cancellationToken = default)
        => _inner.BeginRename(path, newName, cancellationToken);

    /// <inheritdoc />
    public ITaskHandle BeginTouch(
        ItemPath path, TouchOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.BeginTouch(path, options, cancellationToken);

    /// <inheritdoc />
    public ITaskHandle BeginCreateDirectory(
        ItemPath path, CreateDirectoryOptions? options = null, CancellationToken cancellationToken = default)
        => _inner.BeginCreateDirectory(path, options, cancellationToken);

    private static Dictionary<string, string> BuildParameters(CopyOptions? options)
    {
        var p = new Dictionary<string, string>();
        if (options is not null)
        {
            p["recurse"] = options.Recurse.ToString();
            p["force"] = options.Force.ToString();
            p["stopOnError"] = options.StopOnError.ToString();
        }
        return p;
    }

    private static Dictionary<string, string> BuildParameters(MoveOptions? options)
    {
        var p = new Dictionary<string, string>();
        if (options is not null)
        {
            p["force"] = options.Force.ToString();
            p["stopOnError"] = options.StopOnError.ToString();
        }
        return p;
    }

    private static Dictionary<string, string> BuildParameters(TouchOptions? options)
    {
        var p = new Dictionary<string, string>();
        if (options is not null)
        {
            p["createIfMissing"] = options.CreateIfMissing.ToString();
            p["time"] = options.Time?.ToString() ?? "now";
        }
        return p;
    }

    private static Dictionary<string, string> BuildParameters(CreateDirectoryOptions? options)
    {
        var p = new Dictionary<string, string>();
        if (options is not null)
        {
            p["createIntermediate"] = options.CreateIntermediate.ToString();
        }
        return p;
    }
}
