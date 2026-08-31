using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Preview;
using OpenShell.Providers;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>
/// 全局搜索 ViewModel。Per ADR-0030 §6.
/// 接收查询 (300ms 防抖 per ADR-0030 §4 搜索框), 调 <see cref="GlobalSearchCommand"/>
/// (通过 <see cref="ICommandRegistry"/> 解析) 流式收集结果。
/// 支持 Esc 清空 (per ADR-0030 §4 搜索框); 双击结果跳转 (per ADR-0030 §6)。
/// </summary>
/// <remarks>
/// 实现策略 (M3):
/// <list type="bullet">
///   <item>不直接 new <see cref="GlobalSearchCommand"/>; 通过 <see cref="ICommandRegistry"/> 解析并反射调用。</item>
///   <item>查询框输入节流 300ms 后触发搜索。</item>
///   <item>结果聚合显示; 结果数量 / 耗时显示在 UI。</item>
///   <item>GUI host 在 View 层用 Avalonia Window 承载此 ViewModel (占位 view 在 <c>Views/GlobalSearchWindow.cs</c>)。</item>
/// </list>
/// </remarks>
public sealed class GlobalSearchViewModel : ReactiveViewModel
{
    private readonly IProviderRegistry _providers;
    private readonly ICommandRegistry _commands;
    private readonly OpenShell.IHost _host;
    private readonly II18nService? _i18n;
    private readonly FileIndexLifecycleService? _indexLifecycle;
    private readonly Action<IItem>? _onResultSelected;
    private string _query = "";
    private string _statusText = "";
    private string _indexStatusText = "";
    private bool _isSearching;
    private bool _includeContents;
    private CancellationTokenSource? _searchCts;

    // T-312: 缓存最近一次状态 (key + args), 供 LocaleChanged 事件重新翻译。
    private (string Key, object[] Args) _lastStatus = ("", Array.Empty<object>());

    /// <summary>构造 GlobalSearchViewModel。</summary>
    public GlobalSearchViewModel(
        IProviderRegistry providers,
        ICommandRegistry commands,
        OpenShell.IHost host,
        II18nService? i18n = null,
        Action<IItem>? onResultSelected = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _onResultSelected = onResultSelected;

        // T-312: 从全局 DI 容器解析 II18nService (可选; 未注册时为 null, 回退硬编码英文)。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;
        _indexLifecycle = _host.Services.GetService(typeof(FileIndexLifecycleService)) as FileIndexLifecycleService;

        Results = new ObservableCollection<IItem>();
        RefreshIndexStatus();

        // 搜索命令: 防抖 300ms 后触发 (per ADR-0030 §4 搜索框)。
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
        SearchCommand.ThrownExceptions
            .Subscribe(ex => SetStatus("gui.search.error", ex.Message))
            .DisposeWith(Disposables);

        // 输入节流: 当 Query 变化时, 300ms 防抖后触发搜索。
        this.WhenAnyValue(x => x.Query)
            .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
            .Where(_ => !string.IsNullOrWhiteSpace(Query))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SearchCommand.Execute().Subscribe(_ => { }, _ => { }))
            .DisposeWith(Disposables);

        // 清空命令: Esc 清空搜索 (per ADR-0030 §4 搜索框)。
        ClearCommand = ReactiveCommand.Create(() =>
        {
            Query = "";
            Results.Clear();
            _lastStatus = ("", Array.Empty<object>());
            StatusText = "";
            _searchCts?.Cancel();
        });
        ClearCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);

        // 双击跳转命令: 跳到结果项所在目录 (per ADR-0030 §6 双击跳转)。
        NavigateToResultCommand = ReactiveCommand.Create<IItem?>(item =>
        {
            if (item is null) return;
            // 通过 host 输出该项供外部订阅者处理 (与 WriteItemsAsync 一致)。
            _ = _host.WriteItemsAsync(SingleItemStream(item), CancellationToken.None);
            _onResultSelected?.Invoke(item);
        });
        NavigateToResultCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);

        // 取消命令: 内容搜索可能较慢, 必须能主动终止当前 Provider 枚举。
        CancelCommand = ReactiveCommand.Create(() => _searchCts?.Cancel());
        CancelCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);

        // T-312: 订阅 LocaleChanged 事件，动态切换语言后刷新状态文本。
        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
        }
    }

    /// <summary>搜索框文本。300ms 防抖触发搜索。</summary>
    public string Query
    {
        get => _query;
        set => this.RaiseAndSetIfChanged(ref _query, value);
    }

    /// <summary>搜索结果 (流式聚合)。Per ADR-0030 §6: 结果聚合显示。</summary>
    public ObservableCollection<IItem> Results { get; }

    /// <summary>状态文本 (结果数 / 耗时 / 错误)。Per ADR-0030 §4: 显示 "X results in Y ms"。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>是否正在搜索 (控制 spinner / 取消按钮)。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    /// <summary>索引状态。索引热身期间明确提示搜索会回退到实时 provider 枚举。</summary>
    public string IndexStatusText
    {
        get => _indexStatusText;
        private set => this.RaiseAndSetIfChanged(ref _indexStatusText, value);
    }

    /// <summary>是否搜索文件内容。启用后绕过名称索引, 走 Provider 内容读取路径。</summary>
    public bool IncludeContents
    {
        get => _includeContents;
        set => this.RaiseAndSetIfChanged(ref _includeContents, value);
    }

    /// <summary>搜索命令 (节流后自动触发)。</summary>
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }

    /// <summary>清空命令 (Esc 触发)。</summary>
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    /// <summary>取消当前搜索, 不关闭窗口。</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>双击结果跳转命令。Per ADR-0030 §6.</summary>
    public ReactiveCommand<IItem?, Unit> NavigateToResultCommand { get; }

    private async Task SearchAsync()
    {
        // 取消上次搜索 (per ADR-0030 §约束: 搜索结果必须支持取消)。
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        Results.Clear();
        RefreshIndexStatus();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        try
        {
            // 解析 GlobalSearchCommand (per ADR-0004 ICommandRegistry)。
            var desc = _commands.Resolve("search-global") ?? _commands.Resolve("search");
            if (desc is null)
            {
                SetStatus("gui.search.notRegistered");
                return;
            }

            var cmdInstance = (ICommand)Activator.CreateInstance(desc.CommandType)!;
            var args = new GlobalSearchCommand.Args(
                Query: Query,
                Path: (ItemPath?)null,
                IncludeContents: IncludeContents,
                MaxResults: 1000);

            var ctx = new CommandContext
            {
                Providers = _providers,
                Commands = _commands,
                Host = _host,
                CurrentLocation = _host.CurrentLocation,
                CancellationToken = ct,
            };

            var executeMethod = desc.CommandType.GetMethod("ExecuteAsync")!;
            var stream = (IAsyncEnumerable<IItem>)executeMethod.Invoke(cmdInstance, new object[] { args, ctx, ct })!;

            // 结果集合和状态属性直接绑定 Avalonia UI；保留调用方的 UI 同步上下文，
            // 避免异步搜索完成后从线程池线程修改 ObservableCollection。
            await foreach (var item in stream.WithCancellation(ct))
            {
                Results.Add(item);
                count++;
            }

            sw.Stop();
            SetStatus("gui.search.results", count, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // 取消是预期行为。
            SetStatus("gui.search.cancelled", count);
        }
        catch (Exception ex)
        {
            SetStatus("gui.search.error", ex.Message);
        }
        finally
        {
            RefreshIndexStatus();
            IsSearching = false;
        }
    }

    /// <summary>T-312: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key switch
    {
        "gui.search.index.ready" => "Index ready",
        "gui.search.index.warming" => "Index warming; live search active",
        "gui.search.index.live" => "Live search",
        _ => key,
    };

    /// <summary>T-312: 设置状态文本 (翻译后写入), 同时缓存 (key, args) 供 LocaleChanged 重新翻译。</summary>
    private void SetStatus(string key, params object[] args)
    {
        _lastStatus = (key, args);
        StatusText = T(key, args);
    }

    /// <summary>T-312: LocaleChanged 事件处理：重新翻译最近一次状态文本并刷新绑定。</summary>
    private void OnLocaleChanged(object? sender, string e)
    {
        if (!string.IsNullOrEmpty(_lastStatus.Key))
        {
            StatusText = T(_lastStatus.Key, _lastStatus.Args);
        }
        RefreshIndexStatus();
    }

    private void RefreshIndexStatus()
    {
        IndexStatusText = _indexLifecycle is null
            ? T("gui.search.index.live")
            : _indexLifecycle.IsReady
                ? T("gui.search.index.ready")
                : T("gui.search.index.warming");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // T-312: 解除 LocaleChanged 订阅避免泄漏。
            if (_i18n is not null)
            {
                _i18n.LocaleChanged -= OnLocaleChanged;
            }
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>创建只产出单个 item 的异步流 (替代 System.Linq.Async.ToAsyncEnumerable 以避免额外依赖)。</summary>
    private static async IAsyncEnumerable<IItem> SingleItemStream(
        IItem item,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return item;
    }
}
