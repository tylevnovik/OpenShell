using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using OpenShell.Commands;
using OpenShell.Completion;
using OpenShell.I18n;
using ReactiveUI;

namespace OpenShell.Gui.Host.ViewModels;

/// <summary>T-443: 命令面板候选项。Per ADR-0013 §6 / ADR-0023.</summary>
public sealed record CommandPaletteItem(
    string DisplayText,
    string? Description,
    string FullName,
    CompletionKind Kind);

/// <summary>
/// T-443: 命令面板 ViewModel。Per ADR-0013 §6 / ADR-0023.
/// 从 <see cref="ICommandRegistry"/> 枚举所有注册命令，按用户输入过滤，
/// 选中后通过 dispatchLine 执行。复用 <see cref="ICompletionProvider"/> 补全。
/// </summary>
public sealed class CommandPaletteViewModel : ReactiveViewModel
{
    private readonly ICommandRegistry _commands;
    private readonly Func<string, CancellationToken, Task> _dispatchLine;
    private readonly II18nService? _i18n;
    private readonly ICompletionProvider? _completion;

    private string _query = string.Empty;
    private CommandPaletteItem? _selectedItem;
    private string _statusText = string.Empty;

    /// <summary>构造 CommandPaletteViewModel。</summary>
    public CommandPaletteViewModel(
        ICommandRegistry commands,
        Func<string, CancellationToken, Task> dispatchLine,
        II18nService? i18n = null,
        ICompletionProvider? completion = null)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _dispatchLine = dispatchLine ?? throw new ArgumentNullException(nameof(dispatchLine));
        _i18n = i18n;
        _completion = completion;

        // 初始加载所有命令
        LoadAllCommands();

        // 查询防抖 200ms 后过滤
        var canSearch = this.WhenAnyValue(x => x.Query)
            .Select(q => !string.IsNullOrEmpty(q));
        SearchCommand = ReactiveCommand.Create(FilterCommands, canSearch);
        ClearCommand = ReactiveCommand.Create(() => { Query = string.Empty; Items.Clear(); LoadAllCommands(); });
        ExecuteCommand = ReactiveCommand.CreateFromTask<CommandPaletteItem?, Unit>(async item =>
        {
            if (item is null) return Unit.Default;
            await _dispatchLine(item.FullName, CancellationToken.None);
            return Unit.Default;
        });

        SearchCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        ClearCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);
        ExecuteCommand.ThrownExceptions.Subscribe(_ => { }).DisposeWith(Disposables);

        // 查询变化时自动搜索
        this.WhenAnyValue(x => x.Query)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FilterCommands())
            .DisposeWith(Disposables);

        UpdateStatusText();
    }

    /// <summary>用户输入的查询文本。</summary>
    public string Query
    {
        get => _query;
        set => this.RaiseAndSetIfChanged(ref _query, value);
    }

    /// <summary>过滤后的命令候选项列表。</summary>
    public ObservableCollection<CommandPaletteItem> Items { get; } = new();

    /// <summary>当前选中项。</summary>
    public CommandPaletteItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    /// <summary>状态栏文本（匹配数 / 空状态提示）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<CommandPaletteItem?, Unit> ExecuteCommand { get; }

    /// <summary>加载所有注册命令到 Items。</summary>
    private void LoadAllCommands()
    {
        Items.Clear();
        foreach (var cmd in _commands.Registered.OrderBy(c => c.FullName))
        {
            var display = cmd.FullName;
            var desc = cmd.Description;
            Items.Add(new CommandPaletteItem(display, desc, cmd.FullName, CompletionKind.Command));
        }
        UpdateStatusText();
    }

    /// <summary>根据 Query 过滤 Items。空查询时显示全部。</summary>
    private void FilterCommands()
    {
        var q = (Query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(q))
        {
            LoadAllCommands();
            return;
        }

        // 优先用 ICompletionProvider 补全（如果可用）
        if (_completion is not null)
        {
            var completions = _completion.GetCompletions(new CompletionContext(q, q.Length));
            if (completions.Count > 0)
            {
                Items.Clear();
                foreach (var c in completions.Take(100))
                {
                    var fullName = c.CompletionText;
                    // 尝试从命令注册表中查找描述
                    var desc = _commands.Resolve(fullName)?.Description ?? c.Description;
                    Items.Add(new CommandPaletteItem(c.DisplayText, desc, fullName, c.Kind));
                }
                UpdateStatusText();
                return;
            }
        }

        // 回退：简单子串匹配
        Items.Clear();
        foreach (var cmd in _commands.Registered.OrderBy(c => c.FullName))
        {
            if (cmd.FullName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (cmd.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                cmd.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                Items.Add(new CommandPaletteItem(cmd.FullName, cmd.Description, cmd.FullName, CompletionKind.Command));
            }
        }
        UpdateStatusText();
    }

    /// <summary>更新状态栏文本。</summary>
    private void UpdateStatusText()
    {
        if (Items.Count == 0)
        {
            StatusText = T("gui.commandPalette.empty");
        }
        else
        {
            StatusText = $"{Items.Count} command(s)";
        }
    }

    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;
}
