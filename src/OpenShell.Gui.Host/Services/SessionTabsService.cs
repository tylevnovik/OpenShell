using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using OpenShell.Sessions;

namespace OpenShell.Gui.Host.Services;

/// <summary>
/// GUI tab 持久化服务。Per ADR-0034 §11.
/// 在 GUI 启动时从 <see cref="ISessionService.Current"/> 读取 tab 状态并创建对应 tab;
/// 在 tab 变更时 (新建 / 关闭 / 切换位置), 经 1s 防抖后保存回会话。
/// </summary>
/// <remarks>
/// 本服务仅提供 tab 状态的加载与持久化逻辑, 不直接操作 Avalonia View。
/// View 层应订阅 <see cref="TabsLoaded"/> 事件创建 UI tab, 并在 tab 变更时调用 <see cref="UpdateTabs"/>。
/// Per ADR-0034 §11: 关闭 GUI 时保存 tabs, 重开恢复。
/// </remarks>
public sealed class SessionTabsService : IDisposable
{
    /// <summary>tab 变更后防抖保存的延迟: 1 秒。Per ADR-0034 §11.</summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);

    private readonly ISessionService _sessionService;
    private readonly ILogger<SessionTabsService>? _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>防抖定时器: tab 变更后等待 1s 无新变更再触发保存。</summary>
    private Timer? _debounceTimer;

    /// <summary>待保存的最新 tabs 快照 (防抖期间多次变更只保存最终状态)。</summary>
    private volatile IReadOnlyList<TabState>? _pendingTabs;
    private volatile int _pendingActiveTabIndex;

    /// <summary>
    /// 当从会话加载到 tabs 时触发 (启动时或会话切换时)。
    /// GUI View 层订阅此事件创建对应的 UI tab。
    /// </summary>
    public IObservable<TabsLoadedEventArgs> TabsLoaded => _tabsLoaded;
    private readonly Subject<TabsLoadedEventArgs> _tabsLoaded = new();

    private bool _disposed;

    public SessionTabsService(
        ISessionService sessionService,
        ILogger<SessionTabsService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sessionService);
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// 从当前会话加载 tabs 并通知订阅者。Per ADR-0034 §11.
    /// 应在 GUI 启动 (会话加载完成后) 调用。若会话无 tabs, 触发空列表事件 (View 可创建默认 tab)。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    public Task LoadTabsFromSessionAsync(CancellationToken ct = default)
    {
        var session = _sessionService.Current;
        if (session is null)
        {
            _logger?.LogWarning("Cannot load tabs: no active session.");
            return Task.CompletedTask;
        }

        var tabs = session.State.Tabs;
        var activeIndex = session.State.ActiveTabIndex;

        _logger?.LogInformation(
            "Loaded {Count} tab(s) from session '{Name}' (active={Active}).",
            tabs.Count, session.Name, activeIndex);

        _tabsLoaded.OnNext(new TabsLoadedEventArgs(tabs, activeIndex));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 更新会话的 tabs 状态, 并触发防抖保存。Per ADR-0034 §11.
    /// 应在 GUI tab 变更时 (新建 / 关闭 / 位置切换 / split-view 切换) 调用。
    /// 多次快速调用会合并: 仅最后一次调用的 tabs 在 1s 后保存。
    /// </summary>
    /// <param name="tabs">最新的 tab 列表。</param>
    /// <param name="activeTabIndex">当前活跃 tab 索引。</param>
    public void UpdateTabs(IReadOnlyList<TabState> tabs, int activeTabIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tabs);

        // 记录最新 tabs 快照, 重置防抖定时器。
        _pendingTabs = tabs;
        _pendingActiveTabIndex = activeTabIndex;

        _debounceTimer ??= new Timer(OnDebounceElapsed, null, SaveDebounce, Timeout.InfiniteTimeSpan);
        _debounceTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>防抖定时器回调: 将最新 tabs 写入会话并保存。</summary>
    private async void OnDebounceElapsed(object? state)
    {
        var tabs = _pendingTabs;
        if (tabs is null) return;

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _sessionService.Current;
            if (session is null)
            {
                _logger?.LogWarning("Cannot save tabs: no active session.");
                return;
            }

            // 用 with 表达式更新会话状态 (Session / SessionState 均为 immutable record)。
            var updatedState = session.State with
            {
                Tabs = tabs,
                ActiveTabIndex = _pendingActiveTabIndex,
            };
            var updatedSession = session with { State = updatedState };

            _sessionService.UpdateCurrent(updatedSession);
            await _sessionService.SaveAsync().ConfigureAwait(false);

            _logger?.LogDebug(
                "Saved {Count} tab(s) to session '{Name}' (active={Active}).",
                tabs.Count, session.Name, _pendingActiveTabIndex);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save tabs to session.");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 触发最后一次保存 (确保最新 tab 状态落盘)。
        if (_pendingTabs is not null)
        {
            try { OnDebounceElapsed(null); }
            catch { /* best-effort */ }
        }

        _debounceTimer?.Dispose();
        _tabsLoaded.Dispose();
        _saveLock.Dispose();
    }
}

/// <summary>
/// <see cref="SessionTabsService.TabsLoaded"/> 事件参数。Per ADR-0034 §11.
/// </summary>
/// <param name="Tabs">从会话加载的 tab 列表 (可能为空)。</param>
/// <param name="ActiveTabIndex">活跃 tab 索引。</param>
public sealed record TabsLoadedEventArgs(IReadOnlyList<TabState> Tabs, int ActiveTabIndex);
