using System.Reactive.Subjects;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenShell;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Events;
using OpenShell.History;
using OpenShell.Help;
using OpenShell.I18n;
using OpenShell.Interop;
using OpenShell.Items;
using OpenShell.Locations;
using OpenShell.Logging;
using OpenShell.Operations;
using OpenShell.Modules;
using OpenShell.Packaging;
using OpenShell.Clipboard;
using OpenShell.Security;
using OpenShell.Compilation;
using OpenShell.Remoting;
using OpenShell.Packaging.Installation;
using OpenShell.Packaging.Registry;
using OpenShell.Packaging.Signing;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Pipeline;
using OpenShell.Preview;
using OpenShell.Paths;
using OpenShell.Plugins;
using OpenShell.Providers;
using OpenShell.Providers.Archive;
using OpenShell.Providers.FileSystem;
using OpenShell.Providers.Registry;
using OpenShell.Providers.Remote;
using OpenShell.Providers.Variables;
using OpenShell.Runtime;
using OpenShell.Sessions;
using OpenShell.Startup;
using OpenShell.Updates;
using OpenShell.Variables;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Cli.Host;

internal sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;

        var parseResult = CliInvocationParser.Parse(args);
        if (!parseResult.Succeeded)
        {
            CliUsage.WriteError(Console.Error, parseResult.Error!);
            return ExitCodes.InvalidArgument;
        }

        var invocation = parseResult.Options!;
        if (invocation.Mode == CliInvocationMode.Help)
        {
            CliUsage.WriteHelp(Console.Out);
            return ExitCodes.Success;
        }
        if (invocation.Mode == CliInvocationMode.Version)
        {
            CliUsage.WriteVersion(Console.Out);
            return ExitCodes.Success;
        }
        if (invocation.ExecutionPolicy is { } executionPolicy)
        {
            Environment.SetEnvironmentVariable(
                ExecutionPolicyService.ProcessEnvVar,
                executionPolicy.ToString());
        }

        // T-300/D-307: 非交互模式（-Command/-File）下，抑制 info 级别日志输出到 stdout，
        // 避免污染命令输出（等价 pwsh -noprofile 的干净 stdout 行为）。
        // 参考 PS ref ConsoleHost.Tests.ps1：进程级测试依赖 stdout 只包含命令输出。
        var isNonInteractive = invocation.Mode is CliInvocationMode.Command or CliInvocationMode.File;

        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = null;
                });
                // 仅限制用户可见的 Console provider；结构化日志 provider 仍保留完整级别。
                l.AddFilter((providerName, _, level) =>
                    providerName is null
                    || !providerName.Contains("ConsoleLoggerProvider", StringComparison.Ordinal)
                    || level >= LogLevel.Warning);
                // ADR-0031 §1: OpenShellLoggerProvider 通过 DI 注册为 ILoggerProvider (见 ConfigureServices 块),
                // 由框架在构建 LoggerFactory 时自动解析 (需 ILogStore 注入)。
            })
            .ConfigureServices((_, services) =>
            {
                // ADR-0031 §1: 结构化日志存储 (in-memory 环形缓冲区, 默认 1000 条)。
                // 必须先于 IErrorStream / OpenShellLoggerProvider 注册, 后两者依赖它。
                services.AddSingleton<ILogStore, InMemoryLogStore>();
                // ADR-0031 §1: 注册 OpenShellLoggerProvider 为 ILoggerProvider,
                // 由 Microsoft.Extensions.Logging 框架自动发现并加入 LoggerFactory。
                services.AddSingleton<ILoggerProvider, OpenShellLoggerProvider>();

                services.AddSingleton<IProviderRegistry, ProviderRegistry>();
                services.AddSingleton<ICommandRegistry, CommandRegistry>();
                // ADR-0049: ShouldProcess / -WhatIf / -Confirm infrastructure.
                // IConfirmationPrompter abstracts the Y/A/N/L/S/? interactive prompt;
                // ConsoleConfirmationPrompter writes to stderr and reads from stdin.
                // IShouldProcessService holds per-call WhatIf / ConfirmPreference state.
                // T-303: ConsoleConfirmationPrompter 注入 II18nService 翻译确认提示文本。
                services.AddSingleton<IConfirmationPrompter>(sp =>
                    new ConsoleConfirmationPrompter(sp.GetService<II18nService>()));
                services.AddSingleton<IShouldProcessService>(sp =>
                    new ShouldProcessService(sp.GetRequiredService<IConfirmationPrompter>()));
                // ADR-0048 §3.6: ILocationStack singleton shared by Push-Location / Pop-Location.
                services.AddSingleton<ILocationStack, LocationStack>();
                // ADR-0031 §7: IErrorStream 注入 ILogStore, 每次 Write 同步追加 LogLevel=Error 日志,
                // 便于通过 get-log 统一查询错误。
                services.AddSingleton<IErrorStream>(sp =>
                    new InMemoryErrorStream(sp.GetRequiredService<ILogStore>()));
                services.AddSingleton<IAliasRegistry, AliasRegistry>();
                services.AddSingleton<IHelpService, HelpService>();
                services.AddSingleton<IDriveRegistry, InMemoryDriveRegistry>();
                // M5 (ADR-0020): Undo/Redo 装饰器链。
                //   ITrashService → FileTrashService (trash 目录持久化)
                //   IOperationJournal → FileOperationJournal (journal.jsonl 持久化 + 启动加载最近 1000 条)
                //   IOperationEngine → JournalingOperationEngine(TrackingOperationEngine(OperationEngine)) (每次成功操作 append entry + UndoInfo)
                //   IUndoService → InMemoryUndoService (注入 engine + journal + trash + errors, 执行反向操作)
                // 注意: JournalingOperationEngine 必须是 IOperationEngine 的最终绑定, 命令层拿到的是带 journal 的版本。
                //   TrackingOperationEngine 在内层: 每次操作前后调用 IOperationTracker.Increment/Decrement,
                //   供 IPluginLoader.UnloadAsync 等待 in-flight 操作归零后再卸载 ALC (Per ADR-0016 §3)。
                services.AddSingleton<ITrashService, FileTrashService>();
                services.AddSingleton<IOperationJournal, FileOperationJournal>();
                services.AddSingleton<IOperationTracker, OperationTracker>();
                // ADR-0044 §2: 操作引擎装饰器链 + ITaskCenter (BeginXxx / Pause / Resume 支持)。
                // AddOperationsRuntime 注册 ITaskCenter → InMemoryTaskCenter 并重新装配
                // JournalingOperationEngine(TrackingOperationEngine(OperationEngine(providers, trash, taskCenter)))。
                // OperationEngine 构造函数新增 ITaskCenter 参数 (供 BeginXxx 注册任务句柄)。
                services.AddOperationsRuntime();
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
                // ICommandLineExecutor 由 CliHost 实现：把 DispatchAsync 暴露为接口供 ProfileLoader / ReloadProfileCommand 使用。
                services.AddSingleton<ICommandLineExecutor>(sp => sp.GetRequiredService<CliHost>());
                services.AddSingleton<CliHost>();
                services.AddSingleton<OpenShell.IHost>(sp => sp.GetRequiredService<CliHost>());

                // M5: History / Undo / Configuration / IPC / I18n / AutoUpdate 服务注册。
                services.AddSingleton<IHistoryService, FileHistoryService>();

                // ADR-0009: pluggable completion provider shared by the CLI tab handler and the GUI
                // command palette. The aggregating provider composes one source per completion kind.
                // PathCompletionSource needs the live current location owned by CliHost; the lambda
                // resolves CliHost lazily so the provider can be constructed before CliHost is cached.
                services.AddSingleton<ICompletionProvider>(sp =>
                {
                    var commands = sp.GetRequiredService<ICommandRegistry>();
                    var aliases = sp.GetRequiredService<IAliasRegistry>();
                    var providers = sp.GetRequiredService<IProviderRegistry>();
                    var variables = sp.GetRequiredService<IVariableRegistry>();
                    var history = sp.GetRequiredService<IHistoryService>();
                    return new AggregatingCompletionProvider(new ICompletionSource[]
                    {
                        new CommandCompletionSource(commands),
                        new AliasCompletionSource(aliases),
                        new ParameterCompletionSource(commands),
                        new VariableCompletionSource(variables),
                        new PathCompletionSource(providers, () => sp.GetRequiredService<CliHost>().CurrentLocation),
                        new HistoryCompletionSource(history),
                    });
                });
                services.AddSingleton<IUndoService, InMemoryUndoService>();
                services.AddSingleton<IConfigurationService, FileConfigurationService>();
                // IPC: NamedPipeIpcChannel 单例, 端点名由 IpcEndpoints.GetEndpointName() 决定 (含 sessionId)。
                // Per ADR-0021 §2. CLI host 用 HostKind.Cli 标识握手来源。
                services.AddSingleton<IIpcChannel>(sp => new NamedPipeIpcChannel(HostKind.Cli));
                services.AddSingleton<II18nService, ResourceI18nService>();
                // ADR-0037: 自动更新机制。HttpClient 单例 (参考 RegistryClient 模式)；
                // GitHubReleasesUpdateService 默认拉取 openshell-org/openshell 的 GitHub Releases。
                OpenShellPaths.EnsureUpdatesDirs();
                services.AddSingleton<HttpClient>(new HttpClient());
                services.AddSingleton<UpdateStateStore>();
                // ADR-0037 §5: 平台代码签名校验器 (Windows Authenticode / macOS Developer ID)。
                services.AddSingleton<ICodeSignatureVerifier, PlatformCodeSignatureVerifier>();
                // ADR-0037 §12: 企业策略服务 (读取 %ProgramData%/OpenShell/policy.toml 或 /etc/openshell/policy.toml)。
                services.AddSingleton<IEnterprisePolicyService, EnterprisePolicyService>();
                services.AddSingleton<IUpdateService>(sp => new GitHubReleasesUpdateService(
                    sp.GetRequiredService<HttpClient>(),
                    repoOwner: "openshell-org",
                    repoName: "openshell",
                    signatureVerifier: sp.GetService<ICodeSignatureVerifier>(),
                    enterprisePolicy: sp.GetService<IEnterprisePolicyService>()));

                // ADR-0034: 会话与状态恢复。JsonSessionService 单例, 持久化到 ~/.openshell/sessions/。
                // 启动时通过 --session <name> 加载或创建会话 + AcquireLockAsync, 退出时 SaveAsync + ReleaseLockAsync。
                OpenShellPaths.EnsureSessionDirs();
                services.AddSingleton<ISessionService, JsonSessionService>();
                // ADR-0034 §3: 30s 定期自动保存 (IHostedService) + §9 跨机器同步 (WebDAV)。
                // AddSessionRuntime 注册 SessionAutoSaveService (hosted)。
                // AddSessionSync 注册 SessionSyncService; ISessionSyncProvider 按 config.SyncProvider 决定,
                // 默认 "none" 时返回 WebDavSessionSyncProvider 占位 (仅在 sync 命令显式调用时读取 endpoint)。
                services.AddSessionRuntime();
                services.AddSessionSync();
                services.AddSingleton<OpenShell.Sessions.ISessionSyncProvider>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfigurationService>().Config;
                    var endpoint = cfg.SyncEndpoint ?? string.Empty;
                    var user = Environment.GetEnvironmentVariable("OPENSHELL_SYNC_USER");
                    var pass = Environment.GetEnvironmentVariable("OPENSHELL_SYNC_PASS");
                    return new OpenShell.Sessions.WebDavSessionSyncProvider(
                        string.IsNullOrWhiteSpace(endpoint) ? "http://localhost/" : endpoint,
                        username: user,
                        password: pass);
                });

                // ADR-0036: 安全沙箱与权限模型。
                // IAuditService → JsonAuditService (audit.jsonl 追加, 0600 文件权限)
                // ProtectedPathRegistry → 单例, 含内置系统目录默认值
                // ISecurityService → SecurityService 协调器 (role + strictness 来自配置)
                services.AddSingleton<OpenShell.Security.IAuditService, OpenShell.Security.JsonAuditService>();
                services.AddSingleton<OpenShell.Security.ProtectedPathRegistry>();
                services.AddSingleton<OpenShell.Security.ISecurityService>(sp =>
                {
                    // ADR-0036 §9 / §14: role + strictness 来自 config.toml (已由 RunAsync → LoadAsync 加载)。
                    // 解析失败时降级到默认值 (User / Default), 不阻断启动。
                    var cfg = sp.GetRequiredService<IConfigurationService>().Config;
                    var role = Enum.TryParse<OpenShell.Security.SecurityRole>(cfg.SecurityRole, ignoreCase: true, out var r)
                        ? r : OpenShell.Security.SecurityRole.User;
                    var strictness = Enum.TryParse<OpenShell.Security.SecurityStrictness>(cfg.SecurityStrictness, ignoreCase: true, out var s)
                        ? s : OpenShell.Security.SecurityStrictness.Default;
                    return new OpenShell.Security.SecurityService(
                        sp.GetRequiredService<OpenShell.Security.IAuditService>(),
                        sp.GetRequiredService<OpenShell.Security.ProtectedPathRegistry>(),
                        role: role,
                        strictness: strictness);
                });

                // ADR-0036 §5/§11/§14: 安全沙箱运行时 (审计保留期清理、沙箱感知 HTTP 处理器、paranoid 密码提示器)。
                services.AddSecuritySandboxRuntime();

                // M6 (ADR-0040): 事件总线。InProcessEventBus 单例, 进程内 Channel 队列串行处理订阅者。
                // 跨进程桥 (CrossProcessEventBridge) 在 --ipc-server 启用时手动构造 (见下方 startIpcServer 块),
                // 因为 bridge 需要在 IPC 通道就绪后才能 StartAsync 监听对端事件。
                services.AddSingleton<IEventBus, InProcessEventBus>();

                // ADR-0027/0028: Theme / KeyBinding / Menu / Favorites / Recent 服务。
                // 这些服务在 CLI 中主要用于支持命令 (如 set-theme), GUI 中用于实际 UI 渲染。
                services.AddSingleton<OpenShell.Themes.IThemeService, OpenShell.Themes.ThemeService>();
                services.AddSingleton<OpenShell.KeyBindings.IKeyBindingService, OpenShell.KeyBindings.KeyBindingService>();
                services.AddSingleton<OpenShell.Menus.IMenuService>(sp =>
                    new OpenShell.Menus.MenuService(sp.GetRequiredService<ICommandRegistry>().Registered));
                services.AddSingleton<OpenShell.Favorites.IFavoritesService, OpenShell.Favorites.FileFavoritesService>();
                services.AddSingleton<OpenShell.Recent.IRecentService, OpenShell.Recent.FileRecentService>();

                // M4 (ADR-0019): SFTP 远程 Provider 的凭据存储单例。
                // InMemoryCredentialProvider 同时实现 ICredentialProvider (供 SftpProvider 注入) 与
                // 自身的 Set/Remove 方法 (供 set-sftpcredential / remove-sftpcredential 命令调用)。
                // 注册两个别名指向同一实例, 便于命令按具体类型解析。
                var credProvider = new InMemoryCredentialProvider();
                services.AddSingleton(credProvider);
                services.AddSingleton<ICredentialProvider>(credProvider);

                // ADR-0031 §3: FileLogSink 监听 ILogStore.EntryAppended 异步落盘到
                // ~/.openshell/logs/openshell-{date}.log, 每日轮转 + 保留 7 天。
                // 通过 hosted service 包装: StartAsync 时由 DI 解析 FileLogSink 触发其构造 (启动消费者任务 + 订阅事件);
                // StopAsync 时调用 DisposeAsync 等待最后一波 flush 完成。
                services.AddSingleton<FileLogSink>();
                services.AddHostedService<FileLogSinkStarter>();

                // ADR-0031 §5-9, §10: M3+ 可观测性栈 (Serilog 结构化日志 + OpenTelemetry traces/metrics)。
                // - OTLP endpoint 来自 OPENSHELL_OTLP_ENDPOINT 环境变量 (未设置则不导出, 仅本地注册 ActivitySource/Meter)。
                // - 与 M1 OpenShellLoggerProvider / FileLogSink 并行运行, 不影响现有日志路径。
                // - DiagnosticBundleExporter 单例: 供 get-diagnosticbundle 命令调用; 输出目录默认为 cwd。
                var otlpEndpoint = Environment.GetEnvironmentVariable("OPENSHELL_OTLP_ENDPOINT");
                services.AddOpenShellObservability(new ObservabilityOptions
                {
                    OtlpEndpoint = string.IsNullOrWhiteSpace(otlpEndpoint) ? null : otlpEndpoint,
                });
                services.AddSingleton(sp => new DiagnosticBundleExporter(
                    sp.GetRequiredService<ILogStore>(),
                    Environment.CurrentDirectory));

                // M4 (ADR-0016): 插件加载器 (基于 collectible AssemblyLoadContext)。
                // 单例: 维护已加载插件状态, 供 Import-Module / Remove-Module / Get-Module 命令调用。
                // 注入 IOperationTracker 以支持完整 UnloadAsync 流程 (等待 in-flight 操作归零)。
                services.AddSingleton<IPluginLoader>(sp => new PluginLoader(
                    sp.GetRequiredService<IProviderRegistry>(),
                    sp.GetRequiredService<ICommandRegistry>(),
                    sp,
                    sp.GetService<ILogger<PluginLoader>>(),
                    sp.GetService<IOperationTracker>()));

                // ADR-0016 §8: 插件热重载服务 (FileSystemWatcher 监视 plugins/ 目录)。
                // 通过 IHostedService 启动/停止; 配置开关 PluginWatch / PluginHotReload (默认 false)。
                // 配置变更后需重启 host 才生效 (StartAsync 时一次性读取配置)。
                services.AddHostedService<PluginHotReloadService>();

                // ADR-0039: Provider 包生态服务。
                // 顺序: PluginsConfig / ProviderSourceRegistry (配置) → RegistryClient (HTTP, 单例) →
                //       ISignatureVerifier → IProviderInstaller (协调器)。
                // 启动时确保 ~/.openshell/{providers,cache/{downloads,indices}} 目录存在。
                OpenShellPaths.EnsurePackagingDirs();
                services.AddSingleton<PluginsConfig>();
                services.AddSingleton<ProviderSourceRegistry>();
                // RegistryClient 持有 HttpClient 单例; 跨命令复用, 避免端口耗尽。
                services.AddSingleton<RegistryClient>();
                // ADR-0039 §8: Ed25519 detached signature 校验器 (BouncyCastle)。
                // 未签名包根据 sourceIsTrusted 决定 (TrustedSource / Untrusted); 已签名包做真实 Ed25519 验签。
                services.AddSingleton<ISignatureVerifier, Ed25519SignatureVerifier>();
                services.AddSingleton<IProviderInstaller>(sp => new ProviderInstaller(
                    sp.GetRequiredService<ProviderSourceRegistry>(),
                    sp.GetRequiredService<RegistryClient>(),
                    sp.GetRequiredService<ISignatureVerifier>(),
                    sp.GetRequiredService<PluginsConfig>(),
                    sp.GetService<ILogger<ProviderInstaller>>(),
                    providersDir: null,
                    pluginLoader: sp.GetService<IPluginLoader>()));

                // ADR-0039 §9: 主程序更新后的 Provider 兼容性复检器 (由 UpdateOpenShellCommand 或启动钩子调用)。
                services.AddSingleton<PostUpdateCompatibilityChecker>();

                // ADR-0030: 预览与搜索运行时 (6 个 previewer + LRU 缓存 + USN 索引 + SQLite FTS5 + 搜索服务)。
                services.AddPreviewRuntime();

                // ADR-0029: 剪贴板运行时 (CLI host 用进程内 InMemoryClipboardService; 历史默认关闭)。
                // GUI host 调用 AddClipboardRuntime 后再覆盖注册 AvaloniaClipboardService。
                services.AddClipboardRuntime();

                // ADR-0054: 执行策略 (Restricted/RemoteSigned/Unrestricted/Bypass, 4 级优先级)。
                services.AddExecutionPolicy();
                // ADR-0056: 脚本模块注册表 (import/export 缓存, ESM 风格模块系统)。
                services.AddScriptModules();
                // ADR-0058: JIT 编译 (Tier 0 AST 解释 → Tier 1 表达式树编译缓存, 热路径阈值 32)。
                services.AddJitCompilation();
                // ADR-0059: 远程基础设施 (SSH 传输, PSSession 管理, Invoke-Command -ComputerName)。
                services.AddRemoting();
            })
            .Build();

        await host.StartAsync();

        // ADR-0035: i18n 预加载。确保 locales 目录存在, 预加载当前 locale 的用户覆盖文件 (best-effort)。
        // SetLocale 也会按需加载, 此处仅为启动时一次性预载, 避免首次切换 locale 时 IO 延迟。
        var i18n = host.Services.GetService<II18nService>();
        // T-302: Main 作用域的局部翻译函数; i18n 未注册时回退到 key/fallback。
        // C# 局部函数不支持重载, 故合并为单一 params 签名 (无参调用时 args 为空数组)。
        string T(string key, params object[] args) => i18n?.Translate(key, args) ?? key;
        try
        {
            OpenShellPaths.EnsureLocalesDir();
            if (i18n is not null)
            {
                await i18n.LoadLocaleAsync(i18n.CurrentLocale);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(T("tui.i18n.preloadFailed", ex.Message));
        }

        // 加载默认插件目录 (~/.openshell/plugins/*/plugin.manifest.json) 中的第三方插件。
        // Per ADR-0016 §5 (错误恢复): 单个插件加载失败不影响其他插件或主程序启动。
        // 必须在 CliHost 构造之后 (内置 providers/commands 已注册) 调用, 以便插件命令可被 dispatcher 解析。
        try
        {
            // 强制构造 CliHost 单例: CliHost 构造时注册内置 providers / commands, 必须在插件加载之前完成,
            // 否则插件注册的命令可能被内置命令的 alias 索引覆盖顺序影响 (不影响正确性, 仅影响一致性)。
            _ = host.Services.GetRequiredService<CliHost>();
            var pluginLoader = host.Services.GetRequiredService<IPluginLoader>();
            var pluginLogger = host.Services.GetService<ILogger<Program>>();
            var manifests = PluginManifestLoader.DiscoverAll(OpenShellPaths.Plugins, pluginLogger);
            foreach (var manifest in manifests)
            {
                try
                {
                    var loaded = pluginLoader.Load(manifest);
                    if (!isNonInteractive)
                    {
                        Console.WriteLine(
                            T("tui.plugins.loaded", loaded.Name, loaded.Version, loaded.Providers.Count, loaded.CommandTypes.Count));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        T("tui.plugins.loadFailed", manifest.Name, ex.Message));
                    pluginLogger?.LogWarning(ex, "Failed to load plugin '{Name}'.", manifest.Name);
                }
            }
        }
        catch (Exception ex)
        {
            // 整个插件扫描阶段失败 (如目录访问异常) 也不阻塞主程序启动。
            Console.Error.WriteLine(T("tui.plugins.discoveryFailed", ex.Message));
        }

        // 参数已在 Host 创建前完整验证；此处只把快照应用到运行时服务。
        var profileLoader = host.Services.GetRequiredService<IProfileLoader>();
        profileLoader.SkipProfile = invocation.SkipProfile;
        profileLoader.CustomProfilePath = invocation.ProfilePath;
        var startIpcServer = invocation.StartIpcServer;
        var sessionName = invocation.SessionName;
        var commandString = invocation.CommandText;
        var filePath = invocation.FilePath;

        // ADR-0034: 会话加载与崩溃检测。启动时加载或创建会话 + 检测上次崩溃 + AcquireLock。
        // 默认会话名 "default"; --session <name> 指定其他会话。
        var sessionService = host.Services.GetRequiredService<ISessionService>();
        var activeSessionName = sessionName ?? "default";
        try
        {
            var crashResult = await sessionService.DetectCrashAsync(activeSessionName);
            if (crashResult.LockExists && !crashResult.IsProcessAlive)
            {
                Console.Error.WriteLine(
                    T("tui.sessions.crash", activeSessionName, (object)(crashResult.Pid ?? 0), crashResult.MachineName ?? string.Empty));
            }
            else if (crashResult.LockExists && crashResult.IsProcessAlive)
            {
                Console.Error.WriteLine(
                    T("tui.sessions.running", activeSessionName, (object)(crashResult.Pid ?? 0)));
            }
            await sessionService.LoadOrCreateAsync(activeSessionName);
            await sessionService.AcquireLockAsync(activeSessionName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(T("tui.sessions.initFailed", activeSessionName, ex.Message));
        }

        // IPC 服务端模式: --ipc-server 时在 REPL 之前后台启动 IPC 通道。
        // Per ADR-0021 §3. StartAsync 阻塞 (等待客户端连接), 故放后台线程。
        // Per ADR-0040 §4: 同时启动 CrossProcessEventBridge, 把本地 IRemoteRoutableEvent 转发到对端进程。
        IIpcChannel? ipcChannel = null;
        CrossProcessEventBridge? eventBridge = null;
        if (startIpcServer)
        {
            ipcChannel = host.Services.GetRequiredService<IIpcChannel>();
            var eventBus = host.Services.GetRequiredService<IEventBus>();
            // originHostId 用 IPC 端点名 (含 sessionId) 作为 host 唯一标识, 用于跨进程防回环。
            eventBridge = new CrossProcessEventBridge(eventBus, ipcChannel, originHostId: ipcChannel.ChannelName);
            _ = Task.Run(async () =>
            {
                try
                {
                    // bridge.StartAsync 内部会先 StartAsync 通道再启动监听循环。
                    await eventBridge.StartAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(T("tui.ipc.startFailed", ex.Message));
                }
            });
            Console.WriteLine(T("tui.ipc.starting", ipcChannel.ChannelName, NamedPipeIpcChannel.CurrentProtocolVersion));
        }

        try
        {
            var cliHost = host.Services.GetRequiredService<CliHost>();
            // T-300: -Command / -File 非交互执行模式。跳过 REPL 循环，执行后直接退出。
            if (commandString is not null)
                return await cliHost.RunCommandAsync(commandString);
            if (filePath is not null)
                return await cliHost.RunFileAsync(filePath);
            await cliHost.RunAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(T("tui.fatal", ex));
            return ExitCodes.GeneralError;
        }
        finally
        {
            // ADR-0034: 退出时持久化会话状态 + 释放锁 (best-effort, 不阻塞 host 关闭)。
            try
            {
                await sessionService.SaveAsync();
                await sessionService.ReleaseLockAsync(activeSessionName);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(T("tui.sessions.saveFailed", activeSessionName, ex.Message));
            }
            // 退出时停止 IPC 通道与事件桥 (可重入, 安全)。
            if (eventBridge is not null)
            {
                try { await eventBridge.DisposeAsync(); } catch { /* best-effort */ }
            }
            if (ipcChannel is not null)
            {
                try { await ipcChannel.StopAsync(); } catch { /* best-effort */ }
            }
            await host.StopAsync();
        }
    }
}

/// <summary>
/// 启动 <see cref="FileLogSink"/> 的 hosted service wrapper. Per ADR-0031 §3.
/// StartAsync 时由 DI 解析 FileLogSink 触发其构造 (启动消费者任务 + 订阅 EntryAppended);
/// StopAsync 时调用 DisposeAsync 等待最后一波 flush 完成。
/// </summary>
internal sealed class FileLogSinkStarter : IHostedService
{
    private readonly FileLogSink _sink;

    public FileLogSinkStarter(FileLogSink sink)
    {
        _sink = sink;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sink.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 关闭期间异常不应阻塞宿主停止流程。
        }
    }
}

/// <summary>ADR-0050 §1.3: REPL 语法模式。</summary>
internal enum LangMode
{
    /// <summary>Modern 语法 (默认, .osh)。</summary>
    Osh,
    /// <summary>PowerShell 兼容语法 (.ps1)。</summary>
    Ps1,
}

internal sealed class CliHost : OpenShell.IHost, ICommandLineExecutor, IDisposable
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
    private readonly IHistoryService _history;
    private readonly IConfigurationService _config;
    private readonly Subject<IReadOnlyList<IItem>> _selection = new();
    private readonly Subject<OperationProgress> _progress = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<CliHost> _logger;
    private readonly LineEditor _editor;
    private readonly IConfirmationPrompter _confirmationPrompter;
    private readonly II18nService? _i18n;
    // M4 (ADR-0019): SftpProvider 是 IDisposable (持有 SSH.NET 连接池), 需要在 host 关闭时 dispose。
    private readonly SftpProvider? _sftpProvider;
    private CancellationTokenSource _cts = new();
    // ADR-0049 §10: SuspendCallback 嵌套深度计数, 防止无限递归。
    private int _suspendDepth;

    // Session variables. Per ADR-0008.
    private int _lastExitCode = 0;
    private bool _lastSuccess = true;
    private bool _verbose = false;
    private bool _jsonMode = false;

    // D-322: 输出重定向（> file）。非 null 时 WriteOutputLineAsync/WriteItemsAsync 写入此 writer。
    private StreamWriter? _redirectWriter;

    // ADR-0050 §1.3: REPL 当前语法模式。默认 modern (osh); #lang ps1 切换到 PS 兼容模式。
    private LangMode _langMode = LangMode.Osh;

    public CliHost(
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
        IHistoryService history,
        IConfigurationService config,
        IServiceProvider services,
        ILogger<CliHost> logger,
        ICompletionProvider completion,
        IConfirmationPrompter confirmationPrompter)
    {
        _providers = providers;
        _commands = commands;
        _errors = errors;
        _aliases = aliases;
        _help = help;
        _drives = drives;
        _operations = operations;
        _profileLoader = profileLoader;
        _pipeline = pipeline;
        _vars = vars;
        _history = history;
        _config = config;
        _services = services;
        _logger = logger;
        _editor = new LineEditor(completion);
        _confirmationPrompter = confirmationPrompter;
        // T-302: 从 DI 容器解析 II18nService (可选; 测试或精简 host 未注册时为 null, 回退硬编码英文)。
        _i18n = _services.GetService<II18nService>();
        // ADR-0049 §10: 设置 SuspendCallback 进入嵌套 REPL。
        // 委托忽略 target/action 参数; 嵌套循环中读取命令并 DispatchAsync, 直到用户输入 "exit"。
        // 深度计数防止无限递归 (Suspend 内再次触发 ShouldProcess → Suspend)。
        if (_confirmationPrompter is ConsoleConfirmationPrompter consolePrompter)
        {
            consolePrompter.SuspendCallback = (_, _) =>
            {
                if (_suspendDepth >= 8)
                {
                    Console.Error.WriteLine(T("tui.suspend.maxDepth"));
                    return;
                }
                _suspendDepth++;
                try
                {
                    Console.Error.WriteLine(T("tui.suspend.enter"));
                    while (!_cts.IsCancellationRequested)
                    {
                        var nestedLine = _editor.ReadLine(">> ", _cts.Token);
                        if (nestedLine is null) break;
                        nestedLine = nestedLine.Trim();
                        if (nestedLine.Length == 0) continue;
                        if (nestedLine.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
                        try
                        {
                            DispatchAsync(nestedLine, _cts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(T("tui.suspend.error", ex.Message));
                        }
                    }
                }
                finally
                {
                    _suspendDepth--;
                }
            };
        }

        // Bootstrap providers and built-in commands.
        _providers.Register(new FileSystemProvider());
        // ZipArchiveProvider 无条件注册：跨平台支持（zip 格式与 OS 无关）。
        _providers.Register(new ZipArchiveProvider());
        // RegistryProvider 仅 Windows 注册：非 Windows 平台 log warning 后跳过，运行时不抛。
        if (OperatingSystem.IsWindows())
        {
            _providers.Register(new RegistryProvider());
        }
        else
        {
            _logger.LogWarning("{Msg}", T("tui.registryProvider.skipped"));
        }
        // M4 (ADR-0019): SFTP Provider。注入 ICredentialProvider 单例 (InMemoryCredentialProvider)。
        // Provider 实例由 ProviderRegistry 管理, 但 IDisposable 由 CliHost.Dispose 释放。
        var credProvider = _services.GetRequiredService<InMemoryCredentialProvider>();
        _sftpProvider = new SftpProvider(credProvider);
        _providers.Register(_sftpProvider);

        // 内置虚拟盘 Provider：Variable / Env / Function（Per ADR-0047 §10）。
        // 注入 IVariableRegistry 与 IAliasRegistry 单例。
        _providers.Register(new VariableProvider(_vars));
        _providers.Register(new EnvProvider());
        _providers.Register(new FunctionProvider(_aliases));

        // Built-in commands (OpenShell.Core) + SFTP commands (OpenShell.Providers.Remote)。
        // RegisterFromAssembly 自动扫描带 [Verb] 特性的 sealed 类。
        _commands.RegisterFromAssembly(typeof(GetChildItemCommand).Assembly);
        _commands.RegisterFromAssembly(typeof(SftpProvider).Assembly);
        ((AliasRegistry)_aliases).PopulateBuiltinsFrom(_commands);

        // Default location: fs::cwd
        CurrentLocation = new ItemPath
        {
            Provider = "fs",
            InternalPath = Environment.CurrentDirectory.Replace('\\', '/'),
        };

        // 初始化自动变量（ADR-0042）。$HOST / $PWD 在每次命令执行后更新。
        _vars.SetAutomatic("HOST", "Cli");
        _vars.SetAutomatic("PWD", CurrentLocation);
        _vars.SetAutomatic("?", true);
        _vars.SetAutomatic("LASTEXITCODE", 0);
        _vars.SetAutomatic("ERROR", null!);
        _vars.SetAutomatic("ERRORS", Array.Empty<ErrorRecord>());
    }

    /// <summary>
    /// ICommandLineExecutor 实现：把 DispatchAsync 暴露为接口方法，
    /// 供 ProfileLoader / ReloadProfileCommand 通过委托调用而不直接依赖 CliHost。
    /// </summary>
    Task ICommandLineExecutor.ExecuteAsync(string line, CancellationToken cancellationToken)
        => DispatchAsync(line, cancellationToken);

    /// <summary>T-302: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key) => _i18n?.Translate(key) ?? key;

    /// <summary>T-302: 翻译带参数的 key。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    public HostKind Kind => HostKind.Cli;
    public ItemPath CurrentLocation { get; set; }
    public IObservable<IReadOnlyList<IItem>> Selection => _selection;
    public IProgress<OperationProgress> Progress => new ProgressAdapter(_progress);
    public IServiceProvider Services => _services;

    public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        // D-322: 输出重定向 — 若 _redirectWriter 已设置，写入文件而非 stdout。
        if (_redirectWriter is not null)
        {
            _redirectWriter.WriteLine(line);
            return Task.CompletedTask;
        }
        Console.WriteLine(line);
        return Task.CompletedTask;
    }

    public async Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
    {
        var collected = new List<IItem>();
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            collected.Add(item);
            // D-322: 重定向时只写 item name 到文件，不渲染格式化行。
            if (_redirectWriter is not null)
                _redirectWriter.WriteLine(item.Name);
            else
                RenderItemLine(item);
        }

        if (_redirectWriter is not null) return;

        if (collected.Count > 0)
        {
            _selection.OnNext(collected);
            var totalSize = collected.Sum(i => i.Size ?? 0);
            Console.WriteLine(T("tui.items.summary", collected.Count, totalSize));
        }
        else
        {
            Console.WriteLine(T("tui.items.empty"));
        }
    }

    private static void RenderItemLine(IItem item)
    {
        // D-310: Property 类型（如 pwd/Get-Location 输出）显示 Path 属性值（完整路径），
        // 而非 item.Name（仅目录名）。之前 pwd 输出 "openshell-test-xxx" 而非完整路径。
        if (item.Kind == ItemKind.Property)
        {
            var path = item.Properties["Path"]?.ToString() ?? item.Name;
            Console.WriteLine($"  {path}");
            return;
        }
        var icon = item.Kind == ItemKind.Directory ? "DIR " : "    ";
        var size = item.Size is { } s ? $"{s,12:N0} " : "             ";
        var modified = item.Timestamps.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "                   ";
        Console.WriteLine($"  {icon}{size}{modified}  {item.Name}");
    }

    public async Task RunAsync()
    {
        // M5: 加载配置 (ADR-0022)。配置加载失败不阻塞启动, 降级到默认值。
        OpenShellPaths.EnsureRoot();
        try
        {
            await _config.LoadAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(T("tui.warn.config", ex.Message));
        }

        Ansi.WriteBanner(_i18n);
        Console.WriteLine(T("tui.cwd", CurrentLocation.Display));
        Console.WriteLine(T("tui.providers", string.Join(", ", _providers.Registered.Select(p => p.Name))));
        Console.WriteLine(T("tui.commands.count", _commands.Registered.Count));
        Console.WriteLine(T("tui.help.hint"));
        Console.WriteLine();

        // 执行 profile 脚本（ADR-0041）：在 banner 之后、REPL 之前。
        // 加载顺序：用户全局 → 项目级（后者覆盖前者的副作用，如 cd / set-alias）。
        // profile 执行期间产生的错误通过 _errors 流正常显示；执行失败不阻塞 REPL 启动。
        var errCountBeforeProfile = _errors.RecentErrors.Count;
        try
        {
            var profileResult = await _profileLoader.ExecuteAsync(
                line => DispatchAsync(line, _cts.Token),
                _cts.Token);
            if (profileResult.ExecutedFiles.Count > 0)
            {
                Console.WriteLine(
                    T("tui.profile.summary", profileResult.ExecutedFiles.Count, profileResult.LinesExecuted));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // profile 执行失败不阻塞 REPL 启动，仅打印警告后继续。
            Console.Error.WriteLine(T("tui.warn.profile", ex.Message));
            _logger.LogDebug(ex, "profile execution failed");
        }

        // 显示 profile 执行期间产生的错误（统一走 Ansi.WriteError 渲染）。
        var recentAfterProfile = _errors.RecentErrors;
        for (int i = errCountBeforeProfile; i < recentAfterProfile.Count; i++)
        {
            Ansi.WriteError(recentAfterProfile[i]);
        }

        while (!_cts.IsCancellationRequested)
        {
            // 多行输入检测（Per ADR-0008 §多行输入 + ADR-0045 §13-14）：
            // 未闭合的 { / ( / [ / " / ' / here-string / 行尾 \ 或 | 触发续行提示 "..."，
            // 累积直到输入完整后整体送 Parser。
            var line = ReadCompleteLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            // ADR-0050 §1.3: #lang ps1 / #lang osh REPL 指令切换语法模式。
            if (TryHandleLangDirective(line)) continue;

            // 变量查询：$? / $LASTEXITCODE / $env:NAME 等整行变量引用。
            // 修复 ADR-0042：替换 M1 硬编码 if 分支为统一变量解析。
            if (VariableExpander.TryResolve(line, _vars, out var varValue))
            {
                Console.WriteLine(varValue?.ToString() ?? "");
                continue;
            }

            // Strip global switches.
            (line, _verbose, _jsonMode) = StripGlobalSwitches(line);

            // M5: 保存原始输入用于历史记录 (ADR-0020 / ADR-0022 §6)。
            // 记录 alias/variable 展开前的用户实际输入, 便于历史回溯。
            var historyLine = line;

            // Alias/function expansion.
            line = AliasExpander.Expand(line, _aliases);

            // 变量插值（双引号字符串内的 $var 展开）。
            line = VariableExpander.Expand(line, _vars);

            // 记录命令开始前的错误数量，命令结束后只显示新增错误（避免重复显示）。
            // 修复 M1-8：DispatchAsync catch + RunAsync catch + RunAsync flush 三处都写导致重复。
            var errCountBefore = _errors.RecentErrors.Count;
            try
            {
                await DispatchAsync(line, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _errors.Write(ErrorRecord.FromException(ex, phase: ErrorPhase.Operation));
                _logger.LogDebug(ex, "command failed");
            }

            // 统一显示本次命令期间新增的错误（命令内部 ctx.Errors?.Write + DispatchAsync/RunAsync catch 写入）。
            var recent = _errors.RecentErrors;
            for (int i = errCountBefore; i < recent.Count; i++)
            {
                Ansi.WriteError(recent[i]);
            }

            var commandErrors = recent.Skip(errCountBefore).ToArray();
            _lastSuccess = commandErrors.Length == 0;
            _lastExitCode = commandErrors.Length == 0
                ? ExitCodes.Success
                : ExitCodes.For(commandErrors[^1].Category);

            // 更新自动变量（ADR-0042）。每次命令后刷新 $? / $LASTEXITCODE / $PWD / $ERROR / $ERRORS。
            _vars.SetAutomatic("?", _lastSuccess);
            _vars.SetAutomatic("LASTEXITCODE", _lastExitCode);
            _vars.SetAutomatic("PWD", CurrentLocation);
            _vars.SetAutomatic("ERROR", commandErrors.LastOrDefault()!);
            _vars.SetAutomatic("ERRORS", commandErrors);

            // M5: 记录命令历史 (ADR-0020 / ADR-0022 §6)。debounce flush 由 FileHistoryService 处理。
            _history.Add(historyLine, _lastSuccess, _lastExitCode);
        }
    }

    /// <summary>
    /// T-300: 非交互命令执行（参考 pwsh -Command）。执行命令字符串后退出，不进入 REPL。
    /// 命令可含 ; 多语句（; 触发 AST 路径，由 ModernParser 解析为多条语句）。
    /// 输出到 stdout，错误到 stderr，返回退出码（0=成功，非 0=有错误）。
    /// </summary>
    public async Task<int> RunCommandAsync(string command)
    {
        // D-311: 非交互模式（-Command）下抑制 ShouldProcess 确认提示。
        // 参考 pwsh -Command 行为：破坏性命令（rm/mv/cp 覆盖）直接执行，不提示。
        // 不设置则 ConfirmPreference 默认 High，RemoveItemCommand（High impact）会触发
        // ConsoleConfirmationPrompter，而 stdin 重定向时 ReadLine 返回 null → 无限循环。
        if (_services.GetService(typeof(IShouldProcessService)) is ShouldProcessService sp)
            sp.ConfirmPreference = ConfirmPreference.None;

        // 加载配置（不显示 banner）。
        OpenShellPaths.EnsureRoot();
        try { await _config.LoadAsync(_cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine(T("tui.warn.config", ex.Message)); }

        // 执行 profile（除非 --noprofile）。profile 中的 alias/变量在 -Command 中可用。
        if (!_profileLoader.SkipProfile)
        {
            try { await _profileLoader.ExecuteAsync(line => DispatchAsync(line, _cts.Token), _cts.Token); }
            catch (Exception ex) { Console.Error.WriteLine(T("tui.warn.profile", ex.Message)); }
        }

        // Alias/变量展开（与 REPL 一致）。
        command = AliasExpander.Expand(command, _aliases);
        command = VariableExpander.Expand(command, _vars);

        var errCountBefore = _errors.RecentErrors.Count;
        try
        {
            await DispatchAsync(command, _cts.Token);
        }
        catch (OperationCanceledException) { return ExitCodes.Cancelled; }
        catch (Exception ex)
        {
            _errors.Write(ErrorRecord.FromException(ex, phase: ErrorPhase.Operation));
        }

        // 输出错误到 stderr。
        var recent = _errors.RecentErrors;
        for (int i = errCountBefore; i < recent.Count; i++)
        {
            Console.Error.WriteLine($"{recent[i].Category}: {recent[i].Message}");
        }

        return ExitCodeForNewErrors(recent, errCountBefore);
    }

    /// <summary>
    /// T-300: 非交互脚本文件执行（参考 pwsh -File）。加载脚本文件执行后退出。
    /// 按文件后缀选择 parser：.osh → ModernParser，其他 → PowerShellParser。
    /// </summary>
    public async Task<int> RunFileAsync(string path)
    {
        // D-311: 非交互模式（-File）下抑制 ShouldProcess 确认提示（同 RunCommandAsync）。
        if (_services.GetService(typeof(IShouldProcessService)) is ShouldProcessService spFile)
            spFile.ConfirmPreference = ConfirmPreference.None;

        OpenShellPaths.EnsureRoot();
        try { await _config.LoadAsync(_cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine(T("tui.warn.config", ex.Message)); }

        if (!_profileLoader.SkipProfile)
        {
            try { await _profileLoader.ExecuteAsync(line => DispatchAsync(line, _cts.Token), _cts.Token); }
            catch (Exception ex) { Console.Error.WriteLine(T("tui.warn.profile", ex.Message)); }
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return ExitCodes.GeneralError;
        }

        var source = File.ReadAllText(path);
        var ext = Path.GetExtension(path);
        ScriptBlockAst ast;
        try
        {
            ast = string.Equals(ext, ".osh", StringComparison.OrdinalIgnoreCase)
                ? OpenShell.Parsing.ModernParser.Parse(source, path)
                : PowerShellParser.Parse(source, path);
        }
        catch (ParserException ex)
        {
            Console.Error.WriteLine($"Parse error: {ex.Message}");
            return ExitCodes.ParseError;
        }

        // 设置 CurrentModulePath 使脚本内的 import 相对路径能相对脚本文件解析（Per T-206）。
        // D-314: 传递 Operations/Aliases/Help/Drives 服务（同 DispatchAstAsync 的 D-308 修复），
        // 否则脚本中的 mkdir/rm/cp/mv 等命令会失败（"Operation engine is not available"）。
        var execCtx = new OpenShell.Runtime.ExecutionContext(
            variables: _vars,
            commands: _commands,
            errors: _errors,
            host: this,
            providers: _providers)
        {
            Operations = _operations,
            Aliases = _aliases,
            Help = _help,
            Drives = _drives,
        };
        execCtx.CurrentModulePath = Path.GetFullPath(path);
        var evaluator = new Evaluator(execCtx);

        var errCountBefore = _errors.RecentErrors.Count;
        try
        {
            var result = evaluator.Execute(ast);
            if (result.Signal == FlowSignalKind.Exit)
                return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            _errors.Write(ErrorRecord.FromException(ex, phase: ErrorPhase.Operation));
        }

        var recent = _errors.RecentErrors;
        for (int i = errCountBefore; i < recent.Count; i++)
        {
            Console.Error.WriteLine($"{recent[i].Category}: {recent[i].Message}");
        }

        return ExitCodeForNewErrors(recent, errCountBefore);
    }

    private static int ExitCodeForNewErrors(IReadOnlyList<ErrorRecord> errors, int startIndex)
        => startIndex >= errors.Count
            ? ExitCodes.Success
            : ExitCodes.For(errors[^1].Category);

    /// <summary>
    /// 读取完整输入（可能跨多行）。Per ADR-0008 §多行输入 + ADR-0045 §13-14.
    /// <para>
    /// 首行用主提示符读取；若 <see cref="InputCompletenessChecker.IsComplete"/> 返回 false
    /// （未闭合 {/(/[/"/' 或行尾 \ / |），则用 <c>... </c> 续行提示符继续读取，
    /// 直到输入完整。续行期间 Ctrl+C 中止多行输入并返回空串（REPL 继续下一轮）。
    /// </para>
    /// <para>行尾 <c>\</c> 续行：去掉反斜杠后直接拼接（不加换行）。其他续行以 <c>\n</c> 拼接。</para>
    /// </summary>
    /// <returns>完整输入；null 表示取消（退出 REPL）；空串表示 Ctrl+C 中止多行。</returns>
    private string? ReadCompleteLine()
    {
        var first = _editor.ReadLine(CurrentLocation.Display, _cts.Token);
        if (first is null) return null;

        // 单行即完整 → ReadLine 已加入行内历史，直接返回。
        if (InputCompletenessChecker.IsComplete(first))
            return first;

        // 多行输入：撤销 ReadLine 已加入行内历史的首行（部分命令不入历史），
        // 累积完整命令后再统一加入。
        if (first.Length > 0)
            _editor.RemoveLastHistoryEntry();

        var sb = new System.Text.StringBuilder(first);
        while (!InputCompletenessChecker.IsComplete(sb.ToString()))
        {
            var next = _editor.ReadLine("", _cts.Token, isContinuation: true);
            // null = 取消令牌（退出 REPL）。
            if (next is null) return null;
            // Ctrl+C = 中止多行输入（REPL 继续下一轮）。
            // 空串但 WasCancelled=false = 续行时按 Enter 输入空行：应继续累积（追加 \n + 空串）。
            if (_editor.WasCancelled) return string.Empty;

            // 行尾 \ 续行：去掉反斜杠后直接拼接（不加换行，类似 bash）。
            var content = sb.ToString();
            var trimmedEnd = content.TrimEnd();
            if (trimmedEnd.Length > 0 && trimmedEnd[^1] == '\\')
            {
                sb.Clear();
                sb.Append(trimmedEnd, 0, trimmedEnd.Length - 1); // 去掉末尾反斜杠
                sb.Append(next);
            }
            else
            {
                // 其他续行（未闭合分隔符 / 管道）：以换行拼接。
                sb.Append('\n');
                sb.Append(next);
            }
        }

        var result = sb.ToString();
        _editor.AddHistory(result);
        return result;
    }

    private (string, bool verbose, bool json) StripGlobalSwitches(string line)
    {
        var verbose = _verbose;
        var json = _jsonMode;
        // Strip trailing --verbose / --json
        if (line.EndsWith(" --verbose", StringComparison.OrdinalIgnoreCase))
        {
            verbose = true;
            line = line[..^"--verbose".Length].TrimEnd();
        }
        if (line.EndsWith(" --json", StringComparison.OrdinalIgnoreCase))
        {
            json = true;
            line = line[..^"--json".Length].TrimEnd();
        }
        return (line, verbose, json);
    }

    private async Task DispatchAsync(string line, CancellationToken ct)
    {
        // D-322: 输出重定向检测（> file）。在 AST/快路径分发前提取重定向目标。
        // 仅处理简单的 > file 形式（不含引号内的 >）。
        string? redirectFile = null;
        var gtIdx = IndexOfRedirectOperator(line);
        if (gtIdx >= 0)
        {
            redirectFile = line[(gtIdx + 1)..].Trim().Trim('"');
            line = line[..gtIdx].Trim();
        }

        if (!string.IsNullOrEmpty(redirectFile))
        {
            var cwd = CurrentLocation.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
            var fullPath = Path.IsPathRooted(redirectFile)
                ? redirectFile
                : Path.Combine(cwd, redirectFile);
            _redirectWriter = new StreamWriter(fullPath);
        }

        try
        {
            await DispatchCoreAsync(line, ct);
        }
        finally
        {
            // D-322: 命令执行完毕后关闭重定向 writer，恢复 stdout 输出。
            if (_redirectWriter is not null)
            {
                await _redirectWriter.FlushAsync(ct).ConfigureAwait(false);
                await _redirectWriter.DisposeAsync().ConfigureAwait(false);
                _redirectWriter = null;
            }
        }
    }

    /// <summary>D-322: 查找行中重定向操作符 > 的索引（跳过引号内的 >）。</summary>
    private static int IndexOfRedirectOperator(string line)
    {
        var inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') inQuote = !inQuote;
            else if (ch == '>' && !inQuote && (i == 0 || line[i - 1] != '-'))
            {
                // 排除 -> 箭头（虽然不太可能在命令行出现）
                // 确保 > 后面有空格或文件名（不是 >= 等运算符）
                if (i + 1 >= line.Length || line[i + 1] != '=')
                    return i;
            }
        }
        return -1;
    }

    private async Task DispatchCoreAsync(string line, CancellationToken ct)
    {
        // AST 路径：控制流 / 赋值 / 函数定义 / 多语句 / 脚本块。Per ADR-0045 §15.
        // 简单单命令仍走字符串快路径（保留 alias / variable 字符串展开兼容性）。
        if (ShouldUseAstPath(line))
        {
            await DispatchAstAsync(line, ct);
            return;
        }

        var parts = SplitArgs(line);
        if (parts.Count == 0) return;

        // Pipeline 调度：若包含 | 字符，尝试用 PipelineExecutor 串接节点。Per ADR-0010.
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
            // 若 TryExecute 返回 false（无 | 或单节点），继续走单命令路径。
        }

        // Handle --help / -? as quick dispatch to Get-Help.
        if (parts.Count >= 2 && (parts[^1] is "--help" or "-?"))
        {
            var cmdName = parts[0];
            var desc = _commands.Resolve(cmdName);
            if (desc is not null)
            {
                var help = _help.Resolve(desc.FullName) ?? _help.Resolve(desc.Verb + "-" + desc.Noun);
                if (help is not null)
                {
                    await WriteOutputLineAsync(_help.Render(help, HelpMode.Brief));
                    return;
                }
            }
        }

        var cmdName0 = parts[0];
        var desc0 = _commands.Resolve(cmdName0);
        if (desc0 is null)
        {
            var nf = new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = T("error.commandNotFound", cmdName0),
                Operation = cmdName0,
                Phase = ErrorPhase.Parse,
                Suggestion = T("error.commandSuggestion"),
            };
            _errors.Write(nf);
            // 不在此处调用 Ansi.WriteError；由 RunAsync 末尾统一显示新增错误。
            return;
        }

        var cmdInstance = (ICommand)Activator.CreateInstance(desc0.CommandType)!;
        var argsType = desc0.ArgsType;

        // ADR-0049 §8 / §11.2: -WhatIf / -Confirm are common parameters for SupportsShouldProcess
        // commands. Strip them from the token stream before regular argument binding and apply
        // them to the per-call ShouldProcessService state.
        var argTokens = parts.Skip(1).ToArray();
        if (desc0.SupportsShouldProcess)
        {
            (argTokens, var whatIf, var confirm) = StripShouldProcessCommonParams(argTokens);
            if (_services.GetService(typeof(IShouldProcessService)) is ShouldProcessService sp)
            {
                sp.WhatIfPreference = whatIf;
                // D-318: 仅在用户显式传递 -Confirm 时才覆盖 ConfirmPreference。
                // 非交互模式（RunCommandAsync/RunFileAsync）已设 ConfirmPreference=None，
                // 此处无条件覆盖为 High 会导致 rm 等高影响命令触发确认提示 → stdin EOF 无限循环。
                if (confirm)
                    sp.ConfirmPreference = ConfirmPreference.Low;
                sp.ResetSessionConfirmState();
            }
        }

        var args = ParseArgs(desc0, argTokens);
        var ctx = new CommandContext
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

        var executeMethod = desc0.CommandType.GetMethod("ExecuteAsync")!;
        var typedCmd = typeof(CliHost)
            .GetMethod(nameof(ExecuteTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(argsType);
        await (Task)typedCmd.Invoke(null, new object[] { cmdInstance, args, ctx, ct })!;
    }

    /// <summary>
    /// ADR-0050 §1.3: 处理 #lang ps1 / #lang osh REPL 指令。切换语法模式并返回 true 表示已消费。
    /// 不区分大小写; 行首允许空白; 行尾忽略注释。
    /// </summary>
    private bool TryHandleLangDirective(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length < 6 || !trimmed.Slice(0, 5).SequenceEqual("#lang".AsSpan()))
            return false;
        var rest = trimmed.Slice(5).TrimStart();
        // 取第一个 token (到空白为止)。
        int i = 0;
        while (i < rest.Length && !char.IsWhiteSpace(rest[i])) i++;
        var modeToken = rest.Slice(0, i).ToString();
        if (string.Equals(modeToken, "ps1", StringComparison.OrdinalIgnoreCase))
        {
            _langMode = LangMode.Ps1;
            Console.WriteLine(T("tui.lang.ps1"));
            return true;
        }
        if (string.Equals(modeToken, "osh", StringComparison.OrdinalIgnoreCase))
        {
            _langMode = LangMode.Osh;
            Console.WriteLine(T("tui.lang.osh"));
            return true;
        }
        // 未知模式: 不处理, 让 parser 报错。
        return false;
    }

    /// <summary>检测是否应使用 AST 求值路径。Per ADR-0045 §15.</summary>
    /// <remarks>
    /// 控制流关键字开头、赋值、函数定义、多语句（分号）、脚本块开头、& 调用运算符、
    /// foreach/for/while/do/try/if/switch/function/filter/return/break/continue/throw/exit/param/using。
    /// </remarks>
    private static bool ShouldUseAstPath(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length == 0) return false;

        // & 调用运算符 / . dot-source 开头
        if (trimmed[0] == '&' || (trimmed[0] == '.' && (trimmed.Length == 1 || !char.IsDigit(trimmed[1]))))
            return true;

        // { 脚本块开头
        if (trimmed[0] == '{') return true;

        // $var = 赋值（$ 后跟字母，后续含 =）
        if (trimmed[0] == '$')
        {
            // 简单检测：$var 后续含 = 或 .= 等
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx > 0)
            {
                // 排除 == != >= <=（但这些前面有字符，$var= 的 = 前是字母/数字）
                var prev = trimmed[eqIdx - 1];
                if (char.IsLetterOrDigit(prev) || prev == '_' || prev == ']' || prev == '\'')
                    return true;
            }
        }

        // 多语句（分号）
        if (trimmed.Contains(';')) return true;

        // 控制流关键字开头
        foreach (var kw in s_astKeywords)
        {
            if (trimmed.Length >= kw.Length
                && trimmed.Slice(0, kw.Length).Equals(kw, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == kw.Length || !char.IsLetterOrDigit(trimmed[kw.Length])))
                return true;
        }

        return false;
    }

    private static readonly string[] s_astKeywords =
    {
        "if", "elseif", "else", "switch", "while", "do", "until", "for", "foreach",
        "try", "catch", "finally", "function", "filter", "return", "break",
        "continue", "throw", "exit", "param", "using", "trap", "begin", "process", "end",
    };

    /// <summary>AST 求值路径：PowerShellParser → ScriptBlockAst → Evaluator.Execute。Per ADR-0045 §15.</summary>
    private async Task DispatchAstAsync(string line, CancellationToken ct)
    {
        ScriptBlockAst ast;
        try
        {
            // ADR-0050 §1.3: 根据 _langMode 选择 parser。默认 modern (osh), #lang ps1 切换到 PS。
            ast = _langMode == LangMode.Osh
                ? OpenShell.Parsing.ModernParser.Parse(line)
                : PowerShellParser.Parse(line);
        }
        catch (ParserException ex)
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.ParseError,
                Message = $"[{(_langMode == LangMode.Osh ? "modern" : "ps1")}] parse error at line {ex.Span.Start.Line}, col {ex.Span.Start.Column}: {ex.Message}",
                Operation = "parse",
                Phase = ErrorPhase.Parse,
            });
            return;
        }

        // 用户别名展开：替换命令名为展开后的字符串再重新解析。
        // 简化：只在单条 PipelineStatement 时展开命令名。
        line = AliasExpander.Expand(line, _aliases);
        if (line != ast.ToString())
        {
            // 重新解析展开后的行（可能改变命令名）
            try
            {
                ast = _langMode == LangMode.Osh
                    ? OpenShell.Parsing.ModernParser.Parse(line)
                    : PowerShellParser.Parse(line);
            }
            catch (ParserException) { /* 用原 ast */ }
        }

        var execCtx = new OpenShell.Runtime.ExecutionContext(
            variables: _vars,
            commands: _commands,
            errors: _errors,
            host: this,
            providers: _providers,
            cancellationToken: ct)
        {
            // D-308: AST path 之前缺失这些服务，导致 mkdir/rm/cp/mv 等操作引擎命令失败。
            Operations = _operations,
            Aliases = _aliases,
            Help = _help,
            Drives = _drives,
        };
        var evaluator = new Evaluator(execCtx);
        try
        {
            var result = evaluator.Execute(ast);
            if (result.Signal == FlowSignalKind.Exit)
            {
                _lastExitCode = result.ExitCode;
                _lastSuccess = result.ExitCode == 0;
                _vars.SetAutomatic("LASTEXITCODE", _lastExitCode);
                _vars.SetAutomatic("?", _lastSuccess);
            }
            else if (result.Signal == FlowSignalKind.Throw)
            {
                _errors.Write(new ErrorRecord
                {
                    Category = ErrorCategory.OperationFailed,
                    Message = result.ThrownValue?.ToString() ?? "script threw",
                    Operation = "script",
                    Phase = ErrorPhase.Operation,
                });
            }
        }
        catch (OpenShellScriptException ex)
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = ex.Message,
                Operation = "script",
                Phase = ErrorPhase.Operation,
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _errors.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = ex.Message,
                Operation = "script",
                Phase = ErrorPhase.Operation,
            });
        }
    }

    private static async Task ExecuteTypedAsync<TArgs>(ICommand<TArgs> cmd, TArgs args, CommandContext ctx, CancellationToken ct) where TArgs : notnull
    {
        var stream = cmd.ExecuteAsync(args, ctx, ct);
        await ctx.Host.WriteItemsAsync(stream, ct);
    }

    private static object ParseArgs(CommandDescriptor desc, string[] tokens)
    {
        // 先收集所有 bool 参数名（含别名），用于判断 -token 是否应消费下一个 token。
        // 修复 M1-8：`get-childitem -r fs::$test` 中 -r 是 bool 开关不应吃掉 fs::$test。
        var boolParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in desc.Parameters)
        {
            if (p.Type == typeof(bool))
            {
                boolParamNames.Add(p.Name);
                foreach (var a in p.Aliases ?? [])
                    boolParamNames.Add(a.TrimStart('-'));
            }
        }

        var positional = new List<string?>();
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("-") && t.Length > 1)
            {
                var key = t.TrimStart('-');
                string? inlineValue = null;
                // D-317: 处理 -name:value 冒号形式（PowerShell NamedParameter 语法）。
                // 如 -type:directory 应拆分为 key=type, value=directory。
                var colonIdx = key.IndexOf(':');
                if (colonIdx > 0)
                {
                    inlineValue = key[(colonIdx + 1)..];
                    key = key[..colonIdx];
                }
                // bool 开关：设为 "true"，不消费下一个 token。
                if (inlineValue is null && boolParamNames.Contains(key))
                {
                    named[key] = "true";
                    continue;
                }
                if (inlineValue is not null)
                {
                    named[key] = inlineValue;
                }
                else if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("-"))
                {
                    named[key] = tokens[++i];
                }
                else
                {
                    named[key] = "true";
                }
            }
            else
            {
                positional.Add(t);
            }
        }

        var constructor = desc.ArgsType.GetConstructors().First();
        var parameters = constructor.GetParameters();
        var argsValues = new object?[parameters.Length];

        // Positional binding: track which positional indices have been consumed.
        // Per ADR-0046 §5: ScriptBlock positional params accept only { ... } form;
        // non- { } strings fall through to the next positional param at the same position.
        var consumedPositional = new HashSet<int>();

        foreach (var p in parameters)
        {
            var pdesc = desc.Parameters.FirstOrDefault(p2 => string.Equals(p2.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (pdesc is null) { argsValues[Array.IndexOf(parameters, p)] = null; continue; }

            var paramAttr = pdesc.ParameterAttribute;
            if (paramAttr?.Position >= 0 && paramAttr.Position < positional.Count)
            {
                // Try positional binding: iterate from the param's nominal position to find
                // the next unconsumed positional value that converts to the param's type.
                bool bound = false;
                for (int pi = paramAttr.Position; pi < positional.Count; pi++)
                {
                    if (consumedPositional.Contains(pi)) continue;
                    if (positional[pi] is not { } posValue) continue;
                    var converted = ConvertValue(p.ParameterType, posValue);
                    if (converted is null && p.ParameterType != typeof(object))
                    {
                        // ConvertValue returned null = cannot bind (e.g. ScriptBlock with non-{ } string).
                        // Leave this positional value for the next positional param.
                        continue;
                    }
                    argsValues[Array.IndexOf(parameters, p)] = converted;
                    consumedPositional.Add(pi);
                    bound = true;
                    break;
                }
                if (bound) continue;
            }
            else if (named.TryGetValue(p.Name!, out var nValue))
            {
                argsValues[Array.IndexOf(parameters, p)] = ConvertValue(p.ParameterType, nValue!);
                continue;
            }
            else
            {
                var matched = false;
                foreach (var alias in paramAttr?.Aliases ?? [])
                {
                    if (named.TryGetValue(alias.TrimStart('-'), out var aValue))
                    {
                        argsValues[Array.IndexOf(parameters, p)] = ConvertValue(p.ParameterType, aValue!);
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    argsValues[Array.IndexOf(parameters, p)] = p.HasDefaultValue ? p.DefaultValue : null;
                }
            }
        }

        return constructor.Invoke(argsValues) ?? throw new InvalidOperationException("Args constructor returned null.");
    }

    private static object? ConvertValue(Type targetType, string value)
    {
        // Nullable<T> 解包：先剥到 underlying type 再走常规分支。
        // 修复 M1-8：args.Path 为 ItemPath? 时 Convert.ChangeType 抛 InvalidCast。
        if (targetType.IsValueType
            && Nullable.GetUnderlyingType(targetType) is { } underlying)
        {
            return ConvertValue(underlying, value);
        }
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(bool)) return bool.TryParse(value, out var b) ? b : value.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(long)) return long.Parse(value);
        if (targetType == typeof(ItemPath))
        {
            return ItemPath.Parse(value);
        }
        if (targetType == typeof(string[])) return value.Split(',');
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
        // Per ADR-0046 §5: ScriptBlock 参数只接受 { ... } 形式。
        // 非 { } 字符串返回 null 让 positional binding 落到下一个候选参数（如 Expression DSL 字符串）。
        if (targetType == typeof(OpenShell.Runtime.ScriptBlock))
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                // { ... } 形式：去掉外层花括号，解析内部为 ScriptBlockAst，包装为 ScriptBlockExpression + ScriptBlock。
                // 需要 ExecutionContext 用于闭包捕获；此处用空上下文（命令执行时会重建作用域）。
                // TODO(ADR-0046): CliHost 需注入 ExecutionContext 以支持完整闭包语义。
                try
                {
                    var inner = trimmed[1..^1];
                    var scriptAst = OpenShell.Parsing.PowerShellParser.Parse(inner);
                    var blockExpr = new OpenShell.Parsing.Ast.ScriptBlockExpression(
                        scriptAst.Statements,
                        scriptAst.Parameters,
                        scriptAst.Span);
                    return new OpenShell.Runtime.ScriptBlock(blockExpr, new OpenShell.Runtime.ExecutionContext());
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }
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
            // -WhatIf:$false / -Confirm:$false form (PowerShell-style negation).
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

    public void Dispose()
    {
        _cts.Cancel();
        _selection.Dispose();
        _progress.Dispose();
        _editor.Dispose();
        // M4 (ADR-0019): 释放 SFTP 连接池 (关闭所有 SSH.NET SftpClient)。
        _sftpProvider?.Dispose();
    }
}

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

/// <summary>
/// ANSI color helpers. Per ADR-0008.
/// </summary>
internal static class Ansi
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Cyan = "\u001b[36m";
    private const string Yellow = "\u001b[33m";
    private const string Magenta = "\u001b[35m";

    public static bool Enabled => !Console.IsOutputRedirected;

    /// <summary>T-302: 写入 banner。i18n 未注入时回退到硬编码 "OpenShell CLI"。</summary>
    public static void WriteBanner(II18nService? i18n = null)
    {
        var banner = i18n?.Translate("tui.banner") ?? "OpenShell CLI";
        if (!Enabled) { Console.WriteLine(banner); return; }
        // ANSI 着色仅作用于 "OpenShell" 前缀; 后缀 (CLI / 命令行) 保持 Dim。
        // 简化: 若 banner 含空格, 拆分为 前缀 + 后缀 分别着色; 否则整体着色。
        var spaceIdx = banner.IndexOf(' ');
        if (spaceIdx > 0)
        {
            var prefix = banner[..spaceIdx];
            var suffix = banner[(spaceIdx + 1)..];
            Console.WriteLine($"{Bold}{Cyan}{prefix}{Reset} {Dim}{suffix}{Reset}");
        }
        else
        {
            Console.WriteLine($"{Bold}{Cyan}{banner}{Reset}");
        }
    }

    public static void WritePrompt(string location)
    {
        if (!Enabled) { Console.Write($"{location}> "); return; }
        Console.Write($"{Bold}{Green}{location}{Reset}> ");
    }

    /// <summary>续行提示符（多行输入时）。Per ADR-0008 §多行输入 + ADR-0045 §14.</summary>
    public static void WriteContinuationPrompt()
    {
        if (!Enabled) { Console.Write("... "); return; }
        Console.Write($"{Dim}... {Reset}");
    }

    public static void WriteError(ErrorRecord error)
    {
        if (!Enabled)
        {
            Console.Error.WriteLine(error.ToString());
            return;
        }
        Console.Error.WriteLine($"{Red}{error.ToString()}{Reset}");
    }
}

/// <summary>
/// 检查输入文本是否"完整"（所有分隔符已闭合）。Per ADR-0008 §多行输入 + ADR-0045 §13-14.
/// <para>
/// REPL 在每次 Enter 后调用 <see cref="IsComplete"/>；若返回 false 则显示续行提示
/// <c>... </c> 并继续读取下一行，直到输入完整。
/// </para>
/// <para>检测项：未闭合 <c>{</c> / <c>(</c> / <c>[</c> / <c>"</c> / <c>'</c> /
/// here-string <c>@"..."@</c> / <c>@'...'@</c> / 块注释 <c>&lt;#...#&gt;</c> /
/// 行尾 <c>\</c>（续行）/ 行尾 <c>|</c>（管道未结束）。</para>
/// </summary>
internal static class InputCompletenessChecker
{
    /// <summary>判断输入是否完整（可直接送 Parser 求值）。</summary>
    public static bool IsComplete(string text)
    {
        int braces = 0, parens = 0, brackets = 0;
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            char c = text[i];

            // Here-string: @" 或 @' —— 闭合标记 "@ 或 '@ 必须在行首。
            if (c == '@' && i + 1 < len && (text[i + 1] == '"' || text[i + 1] == '\''))
            {
                char quote = text[i + 1];
                int j = i + 2;
                // @" 后必须到行尾（here-string 起始标记后不能有其他内容）
                while (j < len && text[j] != '\n') j++;
                if (j >= len) return false; // 起始标记后无换行 → 未闭合
                j++; // 跳过 \n
                // 逐行查找闭合标记
                bool found = false;
                while (j < len)
                {
                    // 闭合标记必须在行首（前面只有 \n 或位于文本起始）
                    if (text[j] == quote && j + 1 < len && text[j + 1] == '@'
                        && (j == 0 || text[j - 1] == '\n'))
                    {
                        found = true;
                        i = j + 2;
                        break;
                    }
                    // 跳到下一行
                    while (j < len && text[j] != '\n') j++;
                    if (j < len) j++;
                }
                if (!found) return false;
                continue;
            }

            // 块注释: <# ... #>
            if (c == '<' && i + 1 < len && text[i + 1] == '#')
            {
                int end = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 2;
                continue;
            }

            // 行注释: # (仅当不在字符串内时)
            if (c == '#')
            {
                while (i < len && text[i] != '\n') i++;
                continue;
            }

            // 单引号字符串（PS 风格：'' 为转义单引号）
            if (c == '\'')
            {
                i++;
                bool closed = false;
                while (i < len)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < len && text[i + 1] == '\'') { i += 2; continue; }
                        i++; closed = true; break;
                    }
                    i++;
                }
                if (!closed) return false;
                continue;
            }

            // 双引号字符串（PS 风格：` 为转义符，`" 转义引号）
            if (c == '"')
            {
                i++;
                bool closed = false;
                while (i < len)
                {
                    if (text[i] == '`' && i + 1 < len) { i += 2; continue; }
                    if (text[i] == '"') { i++; closed = true; break; }
                    i++;
                }
                if (!closed) return false;
                continue;
            }

            // 分隔符计数
            switch (c)
            {
                case '{': braces++; break;
                case '}': braces--; break;
                case '(': parens++; break;
                case ')': parens--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
            }
            i++;
        }

        // 未闭合分隔符
        if (braces > 0 || parens > 0 || brackets > 0) return false;

        // 行尾续行符：\ 或 |
        var trimmed = text.AsSpan().TrimEnd();
        if (trimmed.Length > 0)
        {
            char last = trimmed[^1];
            if (last == '\\' || last == '|') return false;
        }

        return true;
    }
}

/// <summary>
/// Minimal line editor with up/down history. Per ADR-0008 §3.
/// Tab completion covers command names, aliases, parameter names, and provider paths. Per ADR-0009.
/// </summary>
internal sealed class LineEditor : IDisposable
{
    private readonly List<string> _history = new();
    private int _historyCursor = 0;
    private readonly ICompletionProvider _completion;
    // 当前 prompt 的纯文本部分（不含 ANSI 颜色码），用于 Redraw 重写 + CursorLeft 列号计算。
    // 修复 bug：输入字符后光标前面的 cwd 提示符会消失。
    // 原因：Redraw 用 \r + 空格覆盖整行后只重写了 buf，没重写 prompt。
    private string _promptDisplay = "";
    // 续行模式：多行输入的第二行及之后。使用 "... " 续行提示符，不加入行内历史。
    private bool _isContinuation = false;
    // 最近一次 ReadLine 是否因 Ctrl+C 取消（区分空行 Enter 与 Ctrl+C）。
    public bool WasCancelled { get; private set; }

    public LineEditor(ICompletionProvider completion)
    {
        _completion = completion;
    }

    /// <summary>读取一行输入。Per ADR-0008 §多行输入 + ADR-0045 §14.</summary>
    /// <param name="promptLocation">主提示符位置（cwd 等）。续行模式下忽略。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="isContinuation">是否为续行（多行输入的后续行）。续行使用 <c>... </c> 提示符且不加入行内历史。</param>
    public string? ReadLine(string promptLocation, CancellationToken ct, bool isContinuation = false)
    {
        // Non-interactive fallback: stdin redirected → use plain ReadLine so we can pipe commands.
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine();
            return line;
        }

        _isContinuation = isContinuation;
        WasCancelled = false;
        if (isContinuation)
        {
            // 续行提示符：... （dim 灰色，与主提示符区分）。Per ADR-0045 §14.
            _promptDisplay = "... ";
            Ansi.WriteContinuationPrompt();
        }
        else
        {
            // 记录纯文本 prompt（用于 Redraw 重写 + CursorLeft 列号计算）。
            _promptDisplay = $"{promptLocation}> ";
            Ansi.WritePrompt(promptLocation);
        }

        var buf = new System.Text.StringBuilder();
        var idx = 0;
        ConsoleKeyInfo ki;
        while (true)
        {
            ki = Console.ReadKey(intercept: true);
            if (ct.IsCancellationRequested) return null;

            switch (ki.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    var line = buf.ToString();
                    // 续行行不加入行内历史（仅完整命令入历史，由 CliHost.RunAsync 调用 AddHistory）。
                    if (line.Length > 0 && !_isContinuation)
                    {
                        _history.Add(line);
                        if (_history.Count > 1000) _history.RemoveAt(0);
                        _historyCursor = _history.Count;
                    }
                    return line;

                case ConsoleKey.Backspace:
                    if (idx > 0)
                    {
                        buf.Remove(idx - 1, 1);
                        idx--;
                        Redraw(buf, idx);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (idx < buf.Length)
                    {
                        buf.Remove(idx, 1);
                        Redraw(buf, idx);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (idx > 0) { idx--; Redraw(buf, idx); }
                    break;

                case ConsoleKey.RightArrow:
                    if (idx < buf.Length) { idx++; Redraw(buf, idx); }
                    break;

                case ConsoleKey.Home:
                    idx = 0; Redraw(buf, idx); break;

                case ConsoleKey.End:
                    idx = buf.Length; Redraw(buf, idx); break;

                case ConsoleKey.UpArrow:
                    if (_historyCursor > 0)
                    {
                        _historyCursor--;
                        buf.Clear();
                        buf.Append(_history[_historyCursor]);
                        idx = buf.Length;
                        Redraw(buf, idx);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (_historyCursor < _history.Count)
                    {
                        _historyCursor++;
                        buf.Clear();
                        if (_historyCursor < _history.Count)
                            buf.Append(_history[_historyCursor]);
                        idx = buf.Length;
                        Redraw(buf, idx);
                    }
                    break;

                case ConsoleKey.Escape:
                    buf.Clear(); idx = 0; Redraw(buf, idx); break;

                case ConsoleKey.Tab:
                    var completed = TryComplete(buf.ToString(), idx);
                    if (completed is { } replace)
                    {
                        buf.Clear();
                        buf.Append(replace);
                        idx = buf.Length;
                        Redraw(buf, idx);
                    }
                    else
                    {
                        // Show candidates below the line.
                        var cands = ListCandidates(buf.ToString(), idx);
                        if (cands.Count > 0)
                        {
                            Console.WriteLine();
                            Console.WriteLine(string.Join("  ", cands.Take(20)));
                        }
                    }
                    break;

                case (ConsoleKey)0 when ki.KeyChar == '\u0003': // Ctrl+C
                    Console.WriteLine("^C");
                    WasCancelled = true;
                    return string.Empty;

                default:
                    if (!char.IsControl(ki.KeyChar))
                    {
                        buf.Insert(idx, ki.KeyChar);
                        idx++;
                        Redraw(buf, idx);
                    }
                    break;
            }
        }
    }

    private void Redraw(System.Text.StringBuilder buf, int idx)
    {
        // 回到行首，用空格覆盖整行（包括 prompt 和 buf），再回到行首。
        Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        // 重写 prompt（带 ANSI 颜色）。
        if (_isContinuation)
        {
            // 续行模式：... 提示符不含 "> "，直接写入。
            Ansi.WriteContinuationPrompt();
        }
        else
        {
            Ansi.WritePrompt(_promptDisplay[..^2]);   // 去掉末尾 "> "，因为 Ansi.WritePrompt 会加
        }
        // 重写 buf 内容。
        Console.Write(buf);
        // 光标回到正确位置（prompt 长度 + idx）。
        // Console.CursorLeft 是显示列号，ANSI 颜色码不占显示列。
        if (idx < buf.Length)
        {
            Console.CursorLeft = _promptDisplay.Length + idx;
        }
    }

    /// <summary>添加完整命令到行内历史（多行输入累积后调用）。Per ADR-0008 §多行输入.</summary>
    public void AddHistory(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _history.Add(line);
        if (_history.Count > 1000) _history.RemoveAt(0);
        _historyCursor = _history.Count;
    }

    /// <summary>移除最后一条行内历史记录（多行输入检测到首行不完整时，撤销 ReadLine 已添加的条目）。</summary>
    public void RemoveLastHistoryEntry()
    {
        if (_history.Count > 0)
        {
            _history.RemoveAt(_history.Count - 1);
            _historyCursor = _history.Count;
        }
    }

    /// <summary>
    /// 尝试补全当前 token。Per ADR-0009: 单候选直接替换；多候选补全到公共前缀；
    /// 否则返回 null 让调用方列出候选。返回值为替换后的整行文本。
    /// </summary>
    private string? TryComplete(string input, int cursor)
    {
        var parsed = CompletionParser.Parse(new CompletionContext(input, cursor));
        var candidates = _completion.GetCompletions(new CompletionContext(input, cursor));
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return ReplaceToken(input, parsed, candidates[0].CompletionText);
        }

        var common = CommonPrefix(candidates.Select(c => c.CompletionText).ToList());
        if (common.Length > parsed.Token.Length)
        {
            return ReplaceToken(input, parsed, common);
        }

        return null;
    }

    /// <summary>列出当前 token 的所有补全候选 DisplayText, 供调用方在下方分行展示。</summary>
    private IReadOnlyList<string> ListCandidates(string input, int cursor)
        => _completion
            .GetCompletions(new CompletionContext(input, cursor))
            .Select(c => c.DisplayText)
            .ToList();

    /// <summary>把当前 token 替换为 replacement, 保留 token 之前和之后的文本。</summary>
    private static string ReplaceToken(string input, ParsedCompletion parsed, string replacement)
    {
        var before = parsed.Prefix;
        var tokenEnd = before.Length + parsed.Token.Length;
        var after = tokenEnd >= input.Length ? "" : input[tokenEnd..];
        return before + replacement + after;
    }

    /// <summary>计算多个字符串的公共前缀 (区分大小写)。</summary>
    private static string CommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return "";
        }

        var first = values[0];
        for (var i = 0; i < first.Length; i++)
        {
            var ch = first[i];
            for (var j = 1; j < values.Count; j++)
            {
                if (i >= values[j].Length || values[j][i] != ch)
                {
                    return first[..i];
                }
            }
        }

        return first;
    }

    public void Dispose() { }
}
