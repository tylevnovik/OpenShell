using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Pipeline;
using OpenShell.Providers;
using OpenShell.Providers.FileSystem;
using OpenShell.Providers.Variables;
using OpenShell.Startup;
using OpenShell.Variables;

namespace OpenShell.Gui.Host;

/// <summary>
/// GUI Host 实现 <see cref="OpenShell.IHost"/>。Per ADR-0014 + ADR-0008 host bridge 模式。
/// 与 <c>CliHost</c> 实现同一 <see cref="IHost"/> 契约，命令调用方式一致：
/// <c>Get-ChildItem</c> / <c>Copy-Item</c> 等内置命令复用 Core 的同一实现。
/// Per ADR-0013 §3，ViewModel 不直接 new 命令实例，而是通过 ICommandDispatcher（M3 占位）。
/// </summary>
public sealed class GuiHost : OpenShell.IHost, IDisposable
{
    private readonly IProviderRegistry _providers;
    private readonly ICommandRegistry _commands;
    private readonly IErrorStream _errors;
    private readonly IAliasRegistry _aliases;
    private readonly IHelpService _help;
    private readonly IDriveRegistry _drives;
    private readonly IOperationEngine _operations;
    private readonly IProfileLoader _profileLoader;
    private readonly PipelineExecutor _pipeline;
    private readonly IVariableRegistry _vars;
    private readonly IServiceProvider _services;
    private readonly ILogger<GuiHost>? _logger;
    private readonly II18nService? _i18n;
    private readonly Subject<IReadOnlyList<IItem>> _selection = new();
    private readonly Subject<OperationProgress> _progress = new();
    private CancellationTokenSource _cts = new();
    private ItemPath _currentLocation;

    /// <summary>构造 GuiHost。</summary>
    public GuiHost(
        IProviderRegistry providers,
        ICommandRegistry commands,
        IErrorStream errors,
        IAliasRegistry aliases,
        IHelpService help,
        IDriveRegistry drives,
        IOperationEngine operations,
        IProfileLoader profileLoader,
        PipelineExecutor pipeline,
        IVariableRegistry vars,
        IServiceProvider services,
        ILogger<GuiHost>? logger = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        _help = help ?? throw new ArgumentNullException(nameof(help));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _profileLoader = profileLoader ?? throw new ArgumentNullException(nameof(profileLoader));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _vars = vars ?? throw new ArgumentNullException(nameof(vars));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger;
        // T-304: 从 DI 容器解析 II18nService (可选; 未注册时为 null, 回退硬编码英文)。
        _i18n = _services.GetService(typeof(II18nService)) as II18nService;

        // Bootstrap: 注册 FileSystemProvider + 内置命令 + 别名。参考 CliHost 同款做法。
        _providers.Register(new FileSystemProvider());
        // 内置虚拟盘 Provider：Variable / Env / Function（Per ADR-0047 §10）。
        _providers.Register(new VariableProvider(_vars));
        _providers.Register(new EnvProvider());
        _providers.Register(new FunctionProvider(_aliases));
        _commands.RegisterFromAssembly(typeof(GetChildItemCommand).Assembly);
        ((AliasRegistry)_aliases).PopulateBuiltinsFrom(_commands);

        // 默认位置：fs::cwd。
        _currentLocation = new ItemPath
        {
            Provider = "fs",
            InternalPath = Environment.CurrentDirectory.Replace('\\', '/'),
        };

        // 初始化自动变量（ADR-0042）。GUI host 同步维护 $HOST / $PWD。
        _vars.SetAutomatic("HOST", "Gui");
        _vars.SetAutomatic("PWD", _currentLocation);
        _vars.SetAutomatic("?", true);
        _vars.SetAutomatic("LASTEXITCODE", 0);
        _vars.SetAutomatic("ERROR", null!);
        _vars.SetAutomatic("ERRORS", Array.Empty<ErrorRecord>());
    }

    /// <inheritdoc />
    public HostKind Kind => HostKind.Gui;

    /// <inheritdoc />
    public ItemPath CurrentLocation
    {
        get => _currentLocation;
        set
        {
            _currentLocation = value;
            _vars.SetAutomatic("PWD", value);
        }
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<IItem>> Selection => _selection;

    /// <inheritdoc />
    public IProgress<OperationProgress> Progress => new ProgressAdapter(_progress);

    /// <inheritdoc />
    public IServiceProvider Services => _services;

    /// <summary>T-304: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <summary>取消令牌源。GUI 取消按钮 / Esc 键调 Cancel()。Per ADR-0014 §6.</summary>
    public CancellationTokenSource CommandCancellation => _cts;

    /// <inheritdoc />
    public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        // GUI 无 stdout 概念，M3 阶段仅记录到日志；后续可路由到通知中心（ADR-0040）。
        _logger?.LogInformation("[output] {Line}", line);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
    {
        // Per ADR-0014 §7：流式收集完成后推送 Selection，保证订阅者拿到完整列表。
        var collected = new List<IItem>();
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            collected.Add(item);
        }

        if (collected.Count > 0)
        {
            _selection.OnNext(collected);
        }
    }

    /// <summary>启动 host：执行 profile 脚本（ADR-0041）。在 Avalonia 主窗口创建后调用。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var errCountBeforeProfile = _errors.RecentErrors.Count;
        try
        {
            var profileResult = await _profileLoader.ExecuteAsync(
                line => DispatchAsync(line, _cts.Token),
                cancellationToken);
            if (profileResult.ExecutedFiles.Count > 0)
            {
                _logger?.LogInformation(
                    "profile: {Files} file(s), {Lines} line(s) executed.",
                    profileResult.ExecutedFiles.Count, profileResult.LinesExecuted);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "profile execution failed");
        }
    }

    /// <summary>命令调度入口（M3 简化版，复用 CliHost 的 DispatchAsync 模式）。</summary>
    public async Task DispatchAsync(string line, CancellationToken ct)
    {
        // Pipeline 调度：若包含 | 字符，用 PipelineExecutor 串接节点。Per ADR-0010.
        if (line.Contains('|'))
        {
            var executed = await _pipeline.TryExecuteAsync(
                line,
                ctxFactory: () => new CommandContext
                {
                    Providers = _providers,
                    Commands = _commands,
                    Host = this,
                    CurrentLocation = CurrentLocation,
                    CancellationToken = ct,
                    Errors = _errors,
                    Operations = _operations,
                    Aliases = _aliases,
                    Help = _help,
                    Drives = _drives,
                    Variables = _vars,
                },
                defaultSink: async (ctx, stream) => await WriteItemsAsync(stream, ct),
                ct);
            if (executed) return;
        }

        // 单命令调度：解析命令名 + 参数 + 执行。
        var parts = SplitArgs(line);
        if (parts.Count == 0) return;

        var cmdName = parts[0];
        var desc = _commands.Resolve(cmdName);
        if (desc is null)
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = T("error.commandNotFound", cmdName),
                Operation = cmdName,
                Phase = ErrorPhase.Parse,
                Suggestion = T("error.commandSuggestion"),
            });
            return;
        }

        var cmdInstance = (ICommand)Activator.CreateInstance(desc.CommandType)!;

        // ADR-0049 §8 / §11.2: -WhatIf / -Confirm are common parameters for SupportsShouldProcess
        // commands. Strip them from the token stream before regular argument binding and apply
        // them to the per-call ShouldProcessService state.
        var argTokens = parts.Skip(1).ToArray();
        if (desc.SupportsShouldProcess)
        {
            (argTokens, var whatIf, var confirm) = StripShouldProcessCommonParams(argTokens);
            if (_services.GetService(typeof(IShouldProcessService)) is ShouldProcessService sp)
            {
                sp.WhatIfPreference = whatIf;
                sp.ConfirmPreference = confirm ? ConfirmPreference.Low : ConfirmPreference.High;
                sp.ResetSessionConfirmState();
            }
        }

        var args = ParseArgs(desc, argTokens);
        var ctx2 = new CommandContext
        {
            Providers = _providers,
            Commands = _commands,
            Host = this,
            CurrentLocation = CurrentLocation,
            CancellationToken = ct,
            Errors = _errors,
            Operations = _operations,
            Aliases = _aliases,
            Help = _help,
            Drives = _drives,
            Variables = _vars,
        };

        var executeMethod = desc.CommandType.GetMethod("ExecuteAsync")!;
        var typedCmd = typeof(GuiHost)
            .GetMethod(nameof(ExecuteTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(desc.ArgsType);
        await (Task)typedCmd.Invoke(null, new object[] { cmdInstance, args, ctx2, ct })!;
    }

    private static async Task ExecuteTypedAsync<TArgs>(ICommand<TArgs> cmd, TArgs args, CommandContext ctx, CancellationToken ct) where TArgs : notnull
    {
        var stream = cmd.ExecuteAsync(args, ctx, ct);
        await ctx.Host.WriteItemsAsync(stream, ct);
    }

    private static object ParseArgs(CommandDescriptor desc, string[] tokens)
        => CommandArgumentBinder.Bind(desc, tokens, ConvertValue);

    private static object? ConvertValue(Type targetType, string value)
    {
        if (targetType.IsValueType
            && Nullable.GetUnderlyingType(targetType) is { } underlying)
        {
            return ConvertValue(underlying, value);
        }
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(bool)) return bool.TryParse(value, out var b) ? b : value.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(long)) return long.Parse(value);
        if (targetType == typeof(ItemPath)) return ItemPath.Parse(value);
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
        return Convert.ChangeType(value, targetType);
    }

    private static List<string> SplitArgs(string line)
    {
        var result = new List<string>();
        var inQuote = false;
        var current = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// ADR-0049 §11.2: strip <c>-WhatIf</c> / <c>-Confirm</c> (and their short aliases <c>-wi</c> /
    /// <c>-cf</c>) from the token stream. These are common parameters handled by the host, not the
    /// command. Case-insensitive per ADR-0049 §11. Returns the remaining tokens plus the parsed flags.
    /// </summary>
    private static (string[] remaining, bool whatIf, bool confirm) StripShouldProcessCommonParams(string[] tokens)
    {
        var remaining = new List<string>(tokens.Length);
        var whatIf = false;
        var confirm = false;
        foreach (var t in tokens)
        {
            if (t.Equals("-WhatIf", StringComparison.OrdinalIgnoreCase)
                || t.Equals("-wi", StringComparison.OrdinalIgnoreCase))
            {
                whatIf = true;
                continue;
            }
            if (t.Equals("-Confirm", StringComparison.OrdinalIgnoreCase)
                || t.Equals("-cf", StringComparison.OrdinalIgnoreCase))
            {
                confirm = true;
                continue;
            }
            if (t.StartsWith("-WhatIf:", StringComparison.OrdinalIgnoreCase))
            {
                whatIf = !t.EndsWith(":$false", StringComparison.OrdinalIgnoreCase)
                         && !t.EndsWith(":false", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (t.StartsWith("-Confirm:", StringComparison.OrdinalIgnoreCase))
            {
                confirm = !t.EndsWith(":$false", StringComparison.OrdinalIgnoreCase)
                          && !t.EndsWith(":false", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            remaining.Add(t);
        }
        return (remaining.ToArray(), whatIf, confirm);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _selection.Dispose();
        _progress.Dispose();
    }
}

/// <summary>
/// Progress 桥接器：把 IProgress&lt;T&gt; 调用转发到 IObserver&lt;T&gt;。Per ADR-0014 §3.
/// 与 CliHost.ProgressAdapter 同款实现。
/// </summary>
internal sealed class ProgressAdapter : IProgress<OperationProgress>
{
    private readonly IObserver<OperationProgress> _sink;
    public ProgressAdapter(IObserver<OperationProgress> sink) => _sink = sink;
    public void Report(OperationProgress value)
    {
        _sink.OnNext(value);
        if (value.IsCompleted) _sink.OnCompleted();
    }
}
