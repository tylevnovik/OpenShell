using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenShell.Sessions;

/// <summary>
/// 会话定期自动保存服务。Per ADR-0034 §3 / §14.
/// 作为 <see cref="BackgroundService"/> 运行, 每 30 秒保存一次当前会话状态。
/// 跳过条件: 无活动会话, 或自上次保存以来无变更 (通过 <see cref="Session.LastActive"/> 时间戳追踪 dirty 状态)。
/// </summary>
/// <remarks>
/// 性能: 状态保存 &lt; 50ms (Per ADR-0034 §14), 后台异步执行不阻塞主流程。
/// 频繁保存时合并 (30s 节流, Per ADR-0034 §14)。
/// 保存失败不终止服务: 捕获异常并记录 warning, 下个周期重试。
/// </remarks>
public sealed class SessionAutoSaveService : BackgroundService
{
    /// <summary>自动保存间隔: 30 秒。Per ADR-0034 §3.</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly ISessionService _sessionService;
    private readonly ILogger<SessionAutoSaveService>? _logger;

    /// <summary>
    /// 上次保存时记录的会话 LastActive 时间戳。
    /// 用于 dirty 检测: 若当前会话的 LastActive 与此值不同, 说明有变更需要保存。
    /// 初始值为 MinValue, 确保首次有会话时一定触发一次保存。
    /// </summary>
    private DateTimeOffset _lastSavedLastActive = DateTimeOffset.MinValue;

    public SessionAutoSaveService(
        ISessionService sessionService,
        ILogger<SessionAutoSaveService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sessionService);
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("Session auto-save service started (interval={Interval}s).", SaveInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SaveInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await TrySaveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 保存失败不终止后台服务, 下个周期重试 (Per ADR-0034 §14: 后台异步不阻塞)。
                _logger?.LogWarning(ex, "Session auto-save failed; will retry next cycle.");
            }
        }

        // 退出前做最后一次保存 (确保最新状态落盘)。
        try
        {
            await TrySaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Session auto-save on shutdown failed.");
        }

        _logger?.LogInformation("Session auto-save service stopped.");
    }

    /// <summary>
    /// 执行一次保存检查: 若有活动会话且自上次保存后有变更, 则调用 <see cref="ISessionService.SaveAsync"/>。
    /// </summary>
    private async Task TrySaveAsync(CancellationToken ct)
    {
        var current = _sessionService.Current;
        if (current is null)
        {
            // 无活动会话, 跳过。
            return;
        }

        // dirty 检测: 比较当前会话的 LastActive 与上次保存时记录的值。
        // Session 是不可变 record, 任何状态更新会产生新实例并更新 LastActive。
        if (current.LastActive == _lastSavedLastActive)
        {
            // 无变更, 跳过 (Per ADR-0034 §3: no changes since last save)。
            return;
        }

        await _sessionService.SaveAsync(ct).ConfigureAwait(false);

        // SaveAsync 内部会将 LastActive 更新为 UtcNow, 保存后记录新的 LastActive 供下次比较。
        _lastSavedLastActive = _sessionService.Current?.LastActive ?? DateTimeOffset.UtcNow;
        _logger?.LogDebug("Session '{Name}' auto-saved.", current.Name);
    }
}
