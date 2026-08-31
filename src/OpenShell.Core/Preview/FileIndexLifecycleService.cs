using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenShell.Preview;

/// <summary>
/// 文件索引生命周期协调器。负责启动时恢复内存索引并同步 SQLite FTS，
/// 同时暴露显式刷新入口。索引未就绪时调用方必须回退到 provider 实时枚举。
/// </summary>
public sealed class FileIndexLifecycleService : IHostedService
{
    private readonly UsnJournalIndexer _indexer;
    private readonly FileIndexStore _store;
    private readonly ILogger<FileIndexLifecycleService>? _logger;
    private readonly bool _startBackgroundRefresh;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    private int _ready;

    public FileIndexLifecycleService(
        UsnJournalIndexer indexer,
        FileIndexStore store,
        ILogger<FileIndexLifecycleService>? logger = null,
        bool startBackgroundRefresh = true)
    {
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        _startBackgroundRefresh = startBackgroundRefresh;
    }

    /// <summary>SQLite 索引已可安全用于搜索。</summary>
    public bool IsReady => Volatile.Read(ref _ready) != 0;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _indexer.LoadAsync(cancellationToken).ConfigureAwait(false);

        // 旧版本可能只有 filename-index.db；启动时把已加载记录同步到 FTS 库。
        if (!_store.HasEntries && !_indexer.Files.IsEmpty)
            await _store.RebuildFromIndexerAsync(_indexer, cancellationToken).ConfigureAwait(false);

        Volatile.Write(ref _ready, _store.HasEntries ? 1 : 0);

        if (_startBackgroundRefresh)
        {
            _refreshCts = new CancellationTokenSource();
            var root = Environment.CurrentDirectory;
            _refreshTask = RefreshInBackgroundAsync(root, _refreshCts.Token);
        }
    }

    /// <summary>
    /// 显式刷新指定根目录，并以一次事务重建长期索引。
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Volatile.Write(ref _ready, 0);
        await _indexer.RefreshAsync(roots, cancellationToken).ConfigureAwait(false);
        await _store.RebuildFromIndexerAsync(_indexer, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _ready, _store.HasEntries ? 1 : 0);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshCts?.Cancel();
        if (_refreshTask is not null)
        {
            try
            {
                await _refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 宿主停止超时不再等待后台索引, 由进程退出回收。
            }
        }
        _refreshCts?.Dispose();
        _refreshCts = null;
        _refreshTask = null;
    }

    private async Task RefreshInBackgroundAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(new[] { root }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // 后台索引失败不能影响宿主启动; 搜索会继续使用实时枚举回退。
            Volatile.Write(ref _ready, 0);
            _logger?.LogWarning(ex, "文件索引后台刷新失败: {Root}", root);
        }
    }
}
