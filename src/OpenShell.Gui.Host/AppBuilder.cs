using System.Collections.Generic;
using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenShell.Commands;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using OpenShell.Errors;
using OpenShell.Events;
using OpenShell.Gui.Abstractions;
using OpenShell.Gui.Host.Services;
using OpenShell.Help;
using OpenShell.History;
using OpenShell.I18n;
using OpenShell.Interop;
using OpenShell.Locations;
using OpenShell.Logging;
using OpenShell.Operations;
using OpenShell.Modules;
using OpenShell.Pipeline;
using OpenShell.Plugins;
using OpenShell.Providers;
using OpenShell.Startup;
using OpenShell.Variables;
using OpenShell.Configuration;
using OpenShell.Paths;
using OpenShell.Preview;
using OpenShell.Sessions;
using OpenShell.Security;
using OpenShell.Compilation;
using OpenShell.Remoting;

namespace OpenShell;

internal sealed class Program
{
    /// <summary>
    /// 主程序 DI 容器。Avalonia App 在 OnFrameworkInitializationCompleted 时从此处解析
    /// <see cref="Gui.Host.MainViewModel"/> / <see cref="Gui.Host.GuiHost"/> 等。
    /// </summary>
    internal static IServiceProvider Services { get; private set; } = default!;

    [STAThread]
    public static void Main(string[] args)
    {
        using var host = HostBuilder.Build(args);
        host.StartAsync().GetAwaiter().GetResult();
        Services = host.Services;

        try
        {
            // 启动 Avalonia 桌面生命周期（阻塞）。
            // Profile 脚本执行由 App.OnFrameworkInitializationCompleted 在创建 MainViewModel 后启动,
            // 完成后置 MainViewModel.IsProfileLoading = false (Per ADR-0041)。
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>构建 Avalonia AppBuilder，启用 ReactiveUI 集成。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Gui.Host.App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// Generic Host 构建器。注册所有 Core 服务 + GUI Host 服务。参考 CliHost.Program.cs 同款做法。
/// </summary>
internal static class HostBuilder
{
    // 返回类型显式限定为 Microsoft.Extensions.Hosting.IHost，避免与 OpenShell.IHost 歧义。
    public static Microsoft.Extensions.Hosting.IHost Build(string[] args)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureLogging(l => l.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            }))
            .ConfigureServices((_, services) =>
            {
                // Core 服务注册。与 CliHost 同款。
                services.AddSingleton<IProviderRegistry, ProviderRegistry>();
                services.AddSingleton<ICommandRegistry, CommandRegistry>();
                // ADR-0049: ShouldProcess / -WhatIf / -Confirm infrastructure.
                // For now the GUI host also uses the ConsoleConfirmationPrompter (writes to stderr);
                // a dialog-based prompter can be swapped in later per ADR-0049 §10.
                // T-303: 注入 II18nService 翻译确认提示文本。
                services.AddSingleton<IConfirmationPrompter>(sp =>
                    new ConsoleConfirmationPrompter(sp.GetService<II18nService>()));
                services.AddSingleton<IShouldProcessService>(sp =>
                    new ShouldProcessService(sp.GetRequiredService<IConfirmationPrompter>()));
                // ADR-0048 §3.6: ILocationStack singleton shared by Push-Location / Pop-Location.
                services.AddSingleton<ILocationStack, LocationStack>();
                services.AddSingleton<IErrorStream, InMemoryErrorStream>();
                services.AddSingleton<IAliasRegistry, AliasRegistry>();
                services.AddSingleton<IHelpService, HelpService>();
                services.AddSingleton<IDriveRegistry, InMemoryDriveRegistry>();
                // M5 (ADR-0020): Undo/Redo 装饰器链 (与 CliHost 同款)。
                //   ITrashService → FileTrashService (trash 目录持久化)
                //   IOperationJournal → FileOperationJournal (journal.jsonl 持久化 + 启动加载最近 1000 条)
                //   IOperationEngine → JournalingOperationEngine(TrackingOperationEngine(OperationEngine)) (每次成功操作 append entry + UndoInfo)
                //   IUndoService → InMemoryUndoService (注入 engine + journal + trash + errors, 执行反向操作)
                // 注意: JournalingOperationEngine 必须是 IOperationEngine 的最终绑定, 命令层拿到的是带 journal 的版本。
                //   TrackingOperationEngine 在内层: 每次操作前后调用 IOperationTracker.Increment/Decrement,
                //   供 IPluginLoader.UnloadAsync 等待 in-flight 操作归零后再卸载 ALC (Per ADR-0016 §3)。
                // 修复: GUI Host 之前只注册裸 OperationEngine, 导致 undo/redo 命令在 GUI 下返回 NotImplemented。
                services.AddSingleton<ITrashService, FileTrashService>();
                services.AddSingleton<IOperationJournal, FileOperationJournal>();
                services.AddSingleton<IOperationTracker, OperationTracker>();
                // ADR-0044 §2: 操作引擎装饰器链 + ITaskCenter (BeginXxx / Pause / Resume / 后台任务支持)。
                // AddOperationsRuntime 注册 ITaskCenter → InMemoryTaskCenter (含 IEventBus 注入)
                // 并重新装配 JournalingOperationEngine(TrackingOperationEngine(OperationEngine(providers, trash, taskCenter)))。
                services.AddOperationsRuntime();
                services.AddSingleton<IUndoService, InMemoryUndoService>();
                services.AddSingleton<IProfileLoader, ProfileLoader>();
                services.AddSingleton<PipelineExecutor>(sp =>
                    new PipelineExecutor(
                        sp.GetRequiredService<ICommandRegistry>(),
                        executionContextFactory: () => new OpenShell.Runtime.ExecutionContext(
                            variables: sp.GetService<OpenShell.Variables.IVariableRegistry>(),
                            commands: sp.GetService<ICommandRegistry>(),
                            errors: sp.GetService<IErrorStream>(),
                            host: sp.GetService<OpenShell.IHost>(),
                            providers: sp.GetService<IProviderRegistry>())));
                services.AddSingleton<IVariableRegistry, InMemoryVariableRegistry>();

                // ADR-0048 Tier 2: HttpClient 单例 (供 Invoke-WebRequest / Invoke-RestMethod 使用)。
                services.AddSingleton<HttpClient>(new HttpClient());

                // ADR-0031 §1, §5-9: 结构化日志存储 (in-memory 环形缓冲区) + M3+ 可观测性栈。
                // - ILogStore: DiagnosticBundleExporter / Get-Log 命令 / OpenShellLoggerProvider 依赖。
                // - AddOpenShellObservability: Serilog 结构化日志 + OpenTelemetry traces/metrics。
                //   OTLP endpoint 来自 OPENSHELL_OTLP_ENDPOINT 环境变量 (未设置则不导出)。
                // - DiagnosticBundleExporter: 供 get-diagnosticbundle 命令调用。
                // (GUI Host 暂不注册 OpenShellLoggerProvider / FileLogSink; M1 文件 sink 由 CliHost 负责。)
                services.AddSingleton<ILogStore, InMemoryLogStore>();
                var otlpEndpoint = Environment.GetEnvironmentVariable("OPENSHELL_OTLP_ENDPOINT");
                services.AddOpenShellObservability(new ObservabilityOptions
                {
                    OtlpEndpoint = string.IsNullOrWhiteSpace(otlpEndpoint) ? null : otlpEndpoint,
                });
                services.AddSingleton(sp => new DiagnosticBundleExporter(
                    sp.GetRequiredService<ILogStore>(),
                    Environment.CurrentDirectory));

                // ADR-0009: pluggable completion provider, reused by the GUI command palette.
                // Same source composition as the CLI host. History is optional because the GUI host
                // does not currently register IHistoryService. Current location is read from GuiHost lazily.
                services.AddSingleton<ICompletionProvider>(sp =>
                {
                    var commands = sp.GetRequiredService<ICommandRegistry>();
                    var aliases = sp.GetRequiredService<IAliasRegistry>();
                    var providers = sp.GetRequiredService<IProviderRegistry>();
                    var variables = sp.GetRequiredService<IVariableRegistry>();
                    var history = sp.GetService<IHistoryService>();

                    var sources = new List<ICompletionSource>
                    {
                        new CommandCompletionSource(commands),
                        new AliasCompletionSource(aliases),
                        new ParameterCompletionSource(commands),
                        new VariableCompletionSource(variables),
                        new PathCompletionSource(providers, () => sp.GetRequiredService<OpenShell.IHost>().CurrentLocation),
                    };
                    if (history is not null)
                    {
                        sources.Add(new HistoryCompletionSource(history));
                    }
                    return new AggregatingCompletionProvider(sources);
                });

                // M5 (ADR-0021 + ADR-0040): IPC 通道与事件总线注册。与 CliHost 同款。
                // GUI host 通常作为 IPC 客户端连接到已存在的 CLI 子进程服务端, 但端点名仍由 IpcEndpoints.GetEndpointName() 决定 (含 sessionId)。
                // Per ADR-0021 §2: GUI host 用 HostKind.Gui 标识握手来源。
                // CrossProcessEventBridge 不在此注册 (它需要运行时根据 --ipc-client/--ipc-server 决定是否启动, 见 Main)。
                services.AddSingleton<IIpcChannel>(sp => new NamedPipeIpcChannel(HostKind.Gui));
                services.AddSingleton<IEventBus, InProcessEventBus>();

                // ADR-0027/0028: Theme / KeyBinding / Menu / Favorites / Recent 服务。
                services.AddSingleton<OpenShell.Themes.IThemeService, OpenShell.Themes.ThemeService>();
                services.AddSingleton<OpenShell.KeyBindings.IKeyBindingService, OpenShell.KeyBindings.KeyBindingService>();
                services.AddSingleton<OpenShell.Menus.IMenuService>(sp =>
                    new OpenShell.Menus.MenuService(sp.GetRequiredService<ICommandRegistry>().Registered));
                services.AddSingleton<OpenShell.Favorites.IFavoritesService, OpenShell.Favorites.FileFavoritesService>();
                services.AddSingleton<OpenShell.Recent.IRecentService, OpenShell.Recent.FileRecentService>();

                // ADR-0034: 会话与状态恢复 (GUI tab 持久化 + 30s 自动保存) + ADR-0030 预览/搜索。
                // IConfigurationService 先于 ISessionService 注册 (JsonSessionService / sync 均可能读取配置)。
                services.AddSingleton<IConfigurationService, FileConfigurationService>();
                OpenShellPaths.EnsureSessionDirs();
                services.AddSingleton<ISessionService, JsonSessionService>();
                services.AddSessionRuntime();
                services.AddSingleton<SessionTabsService>();

                // ADR-0030: 预览与搜索运行时 (6 个 previewer + LRU 缓存 + USN 索引 + SQLite FTS5 + 搜索服务)。
                // IQuickLookWindow 的 GUI 实现由 Views 层提供 (Core 不引用 Avalonia)。
                services.AddPreviewRuntime();
                services.AddSingleton<OpenShell.Commands.Builtins.IQuickLookWindow, OpenShell.Gui.Host.Views.QuickLookWindow>();

                // ADR-0035: i18n 服务。与 CliHost 同款, 注册为单例 ResourceI18nService。
                // SetLocale 按需加载用户 locale 文件; GUI 启动时不强制预加载 (避免阻塞 UI 线程)。
                services.AddSingleton<II18nService, ResourceI18nService>();

                // T-401 (F-05): 剪贴板与拖拽服务注册。
                // AvaloniaClipboardService 实现 IClipboardService，无构造函数依赖（运行时从 Application.Current 解析 Avalonia 剪贴板）。
                // AvaloniaDragDropService 依赖 CommandDispatchingDragDropService（Core 层，需 dispatcher + contextFactory）。
                services.AddSingleton<OpenShell.Clipboard.IClipboardService, OpenShell.Gui.Host.Services.AvaloniaClipboardService>();
                services.AddSingleton<OpenShell.Clipboard.CommandDispatchingDragDropService>(sp =>
                {
                    // dispatcher: 委托到 GuiHost.DispatchAsync（按命令行调度命令）。
                    // GuiHost.DispatchAsync 返回 Task（无结果），这里适配为 Task<IAsyncEnumerable<IItem>>（返回空流）。
                    var guiHost = sp.GetRequiredService<Gui.Host.GuiHost>();
                    Func<string, OpenShell.Commands.CommandContext, CancellationToken, Task<IAsyncEnumerable<OpenShell.Items.IItem>>> dispatcher =
                        async (line, ctx, ct) =>
                        {
                            await guiHost.DispatchAsync(line, ct);
                            return EmptyItemStream();
                        };
                    // CommandContext 必填成员: Providers / Commands / Host / CurrentLocation。
                    // 从 DI 解析 IProviderRegistry / ICommandRegistry；GuiHost 实现 IHost，提供 CurrentLocation。
                    Func<OpenShell.Commands.CommandContext> contextFactory = () => new OpenShell.Commands.CommandContext
                    {
                        Providers = sp.GetRequiredService<IProviderRegistry>(),
                        Commands = sp.GetRequiredService<ICommandRegistry>(),
                        Host = guiHost,
                        CurrentLocation = guiHost.CurrentLocation,
                        Errors = sp.GetService<IErrorStream>(),
                        Operations = sp.GetService<IOperationEngine>(),
                    };
                    return new OpenShell.Clipboard.CommandDispatchingDragDropService(dispatcher, contextFactory);
                });
                services.AddSingleton<OpenShell.Clipboard.IDragDropService, OpenShell.Gui.Host.Services.AvaloniaDragDropService>();

                // M4 (ADR-0016): 插件加载器 (与 CliHost 同款, 含 IOperationTracker 注入)。
                // GUI Host 暂不注册 PluginHotReloadService (依赖 IConfigurationService, GUI Host 未注册);
                // 配置基础设施补齐后再启用。
                services.AddSingleton<IPluginLoader>(sp => new PluginLoader(
                    sp.GetRequiredService<IProviderRegistry>(),
                    sp.GetRequiredService<ICommandRegistry>(),
                    sp,
                    sp.GetService<ILogger<PluginLoader>>(),
                    sp.GetService<IOperationTracker>()));

                // GUI 特有：IDialogService（Avalonia 实现，Per ADR-0043 §3）。
                // AvaloniaDialogService 懒解析 MainWindow（Application.Current.ApplicationLifetime.MainWindow），
                // DI 容器在 MainWindow 创建前构建，运行时调用 ShowXxxAsync 时 MainWindow 已就绪。
                // Per ADR-0043 §2: IDialogHost 用于 ShowCustomAsync 委托, 必须先于 IDialogService 注册。
                // ITaskCenter 已由 AddOperationsRuntime() 注册 (含 IEventBus 注入, Per ADR-0044 §1)。
                services.AddSingleton<IDialogHost, AvaloniaDialogHost>();
                services.AddSingleton<IDialogService, AvaloniaDialogService>();

                // ADR-0054: 执行策略 + ADR-0056: 脚本模块系统 + ADR-0058: JIT 编译 + ADR-0059: 远程基础设施。
                services.AddExecutionPolicy();
                services.AddScriptModules();
                services.AddJitCompilation();
                services.AddRemoting();

                // GuiHost 实现作为单例，同时是 IHost 的实现。
                services.AddSingleton<Gui.Host.GuiHost>(sp =>
                {
                    var host = new Gui.Host.GuiHost(
                        sp.GetRequiredService<IProviderRegistry>(),
                        sp.GetRequiredService<ICommandRegistry>(),
                        sp.GetRequiredService<IErrorStream>(),
                        sp.GetRequiredService<IAliasRegistry>(),
                        sp.GetRequiredService<IHelpService>(),
                        sp.GetRequiredService<IDriveRegistry>(),
                        sp.GetRequiredService<IOperationEngine>(),
                        sp.GetRequiredService<IProfileLoader>(),
                        sp.GetRequiredService<PipelineExecutor>(),
                        sp.GetRequiredService<IVariableRegistry>(),
                        sp);
                    return host;
                });
                services.AddSingleton<OpenShell.IHost>(sp => sp.GetRequiredService<Gui.Host.GuiHost>());
            })
            .Build();
    }

    /// <summary>返回空的 IItem 异步流，用于 dispatcher 适配（GuiHost.DispatchAsync 无返回值）。</summary>
    private static async IAsyncEnumerable<OpenShell.Items.IItem> EmptyItemStream()
    {
        yield break;
    }
}
