using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Events;
using OpenShell.Help;
using OpenShell.History;
using OpenShell.Locations;
using OpenShell.Logging;
using OpenShell.Modules;
using OpenShell.Operations;
using OpenShell.Pipeline;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Security;
using OpenShell.Variables;

namespace OpenShell.TestUtils;

/// <summary>
/// 测试用 Host 构建器。Per ADR-0033: 集成测试用真实实现（非 mock），临时目录隔离。
/// 构建最小可用的 IProviderRegistry + ICommandRegistry + IOperationEngine + IErrorStream +
/// IAliasRegistry + IHelpService + IDriveRegistry + IVariableRegistry + PipelineExecutor + IConfigurationService。
/// 返回 IServiceProvider，供集成测试用。
/// </summary>
public sealed class TestHostBuilder
{
    private readonly TempDir _tempDir;
    private readonly ServiceCollection _services = new();
    private readonly ProviderRegistry _providers = new();
    private readonly CommandRegistry _commands = new();
    private readonly AliasRegistry _aliases;
    private readonly InMemoryErrorStream _errors;
    private readonly InMemoryLogStore _logStore = new();
    private readonly InMemoryDriveRegistry _drives = new();
    private readonly InMemoryVariableRegistry _variables = new();
    private readonly IOperationEngine _operations;
    private readonly HelpService _help;
    private readonly PipelineExecutor _pipeline;
    private readonly FileConfigurationService _config;
    private readonly InProcessEventBus _eventBus = new();
    private readonly LocationStack _locationStack = new();
    private ItemPath _currentLocation;
    private IServiceProvider? _builtProvider;
    private TestHost? _lastHost;

    public TestHostBuilder(TempDir tempDir)
    {
        _tempDir = tempDir;
        // 在临时目录下建立 .openshell 子目录作为 user-global 配置位置，避免污染用户环境。
        var userGlobalDir = System.IO.Path.Combine(tempDir.FullPath, ".openshell");
        System.IO.Directory.CreateDirectory(userGlobalDir);
        _aliases = new AliasRegistry(userGlobalDir: userGlobalDir, projectDir: userGlobalDir);
        _errors = new InMemoryErrorStream(_logStore);
        _help = new HelpService(_commands);
        _pipeline = new PipelineExecutor(_commands);
        _config = new FileConfigurationService(System.IO.Path.Combine(userGlobalDir, "config.toml"));

        // OperationEngine 用裸实现，不挂 JournalingOperationEngine（集成测试不需要 journal 持久化）。
        _operations = new OperationEngine(_providers);

        _currentLocation = new ItemPath
        {
            Provider = "fs",
            InternalPath = tempDir.FullPath.Replace('\\', '/'),
        };
    }

    /// <summary>注册一个 Provider。</summary>
    public TestHostBuilder WithProvider(IProvider provider)
    {
        _providers.Register(provider);
        return this;
    }

    /// <summary>从指定程序集扫描注册命令。</summary>
    public TestHostBuilder RegisterCommandsFromAssembly(System.Reflection.Assembly assembly)
    {
        _commands.RegisterFromAssembly(assembly);
        return this;
    }

    /// <summary>从已注册的命令填充 builtin alias。</summary>
    public TestHostBuilder PopulateBuiltins()
    {
        _aliases.PopulateBuiltinsFrom(_commands);
        return this;
    }

    /// <summary>设置当前工作目录（默认为临时目录）。</summary>
    public TestHostBuilder WithCurrentLocation(ItemPath location)
    {
        _currentLocation = location;
        return this;
    }

    /// <summary>构建 ServiceProvider。</summary>
    public IServiceProvider Build()
    {
        _services.AddSingleton<IProviderRegistry>(_providers);
        _services.AddSingleton<ICommandRegistry>(_commands);
        _services.AddSingleton<IAliasRegistry>(_aliases);
        _services.AddSingleton<IErrorStream>(_errors);
        _services.AddSingleton<ILogStore>(_logStore);
        _services.AddSingleton<IHelpService>(_help);
        _services.AddSingleton<IDriveRegistry>(_drives);
        _services.AddSingleton<IVariableRegistry>(_variables);
        _services.AddSingleton<IOperationEngine>(_operations);
        _services.AddSingleton<PipelineExecutor>(_pipeline);
        _services.AddSingleton<IConfigurationService>(_config);
        _services.AddSingleton<IEventBus>(_eventBus);
        _services.AddSingleton<ILocationStack>(_locationStack);
        _services.AddSingleton<IHost>(sp => new TestHost(_currentLocation, _locationStack, sp));

        // ADR-0054: 执行策略 (测试默认 Bypass, 不阻塞脚本加载) + ADR-0056: 脚本模块注册表。
        _services.AddExecutionPolicy();
        _services.AddScriptModules();

        var provider = _services.BuildServiceProvider();
        // 触发 IHost 解析，让 TestHost 持有完整 ServiceProvider（供 ctx.Host.Services 解析 ModuleRegistry 等）。
        _ = provider.GetRequiredService<IHost>();
        return provider;
    }

    /// <summary>暴露 ProviderRegistry 便于直接注册 provider。</summary>
    public ProviderRegistry Providers => _providers;

    /// <summary>暴露 CommandRegistry 便于注册命令。</summary>
    public CommandRegistry Commands => _commands;

    /// <summary>暴露 AliasRegistry 便于 PopulateBuiltins。</summary>
    public AliasRegistry Aliases => _aliases;

    /// <summary>暴露 OperationEngine（非 journal 版本）。</summary>
    public IOperationEngine Operations => _operations;

    /// <summary>当前工作目录。</summary>
    public ItemPath CurrentLocation => _currentLocation;

    /// <summary>临时目录。</summary>
    public TempDir TempDir => _tempDir;

    /// <summary>暴露 LocationStack 便于测试断言栈状态。</summary>
    public LocationStack LocationStack => _locationStack;

    /// <summary>构建一个 CommandContext 用于命令执行。</summary>
    public CommandContext CreateCommandContext()
    {
        // 构建一个最小 ServiceProvider 让命令可以通过 ctx.Host.Services 解析 ILocationStack。
        // 不调用 Build() 以避免破坏现有测试对 TestHost 状态的假设；ServiceProvider 仅含必要服务。
        _builtProvider ??= BuildMinimalProvider();
        _lastHost = new TestHost(_currentLocation, _locationStack, _builtProvider);
        return new CommandContext
        {
            Providers = _providers,
            Commands = _commands,
            Host = _lastHost,
            CurrentLocation = _currentLocation,
            Errors = _errors,
            Operations = _operations,
            Aliases = _aliases,
            Help = _help,
            Drives = _drives,
            Variables = _variables,
        };
    }

    /// <summary>最后一次 <see cref="CreateCommandContext"/> 创建的 TestHost 捕获的进度报告。</summary>
    public IReadOnlyList<OperationProgress>? CapturedProgress => _lastHost?.CapturedProgress;

    /// <summary>最后一次 <see cref="CreateCommandContext"/> 创建的 TestHost 捕获的输出行。</summary>
    public IReadOnlyList<string>? CapturedOutput => _lastHost?.CapturedOutput;

    private IServiceProvider BuildMinimalProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocationStack>(_locationStack);
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// 测试用最小 IHost 实现。仅提供 WriteOutputLineAsync + CurrentLocation，
/// 不实现 Selection / Services（命令测试中无需求）。
/// Progress 返回可捕获的同步实现 (CapturingProgress), 便于进度报告断言。
/// </summary>
internal sealed class TestHost : IHost
{
    private readonly List<string> _output = new();
    private readonly IServiceProvider? _services;
    private readonly CapturingProgress _progress = new();

    public TestHost(ItemPath currentLocation, ILocationStack? locationStack = null, IServiceProvider? services = null)
    {
        CurrentLocation = currentLocation;
        _services = services;
    }

    public HostKind Kind => HostKind.Cli;

    public ItemPath CurrentLocation { get; set; }

    public IObservable<IReadOnlyList<Items.IItem>> Selection
        => new EmptyObservable<IReadOnlyList<Items.IItem>>();

    public IProgress<OperationProgress> Progress => _progress;

    /// <summary>捕获的进度报告 (线程安全, 便于断言)。Per ADR-0030 §5.</summary>
    public IReadOnlyList<OperationProgress> CapturedProgress => _progress.Reports;

    public IServiceProvider Services
        => _services ?? throw new NotSupportedException("TestHost does not expose Services.");

    public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        _output.Add(line);
        return Task.CompletedTask;
    }

    public async Task WriteItemsAsync(IAsyncEnumerable<Items.IItem> items, CancellationToken cancellationToken = default)
    {
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            _output.Add(item.Name);
        }
    }

    /// <summary>捕获的输出行（便于断言）。</summary>
    public IReadOnlyList<string> CapturedOutput => _output;
}

/// <summary>空 IObservable 实现：不发送任何值，订阅即返回空 IDisposable。</summary>
internal sealed class EmptyObservable<T> : IObservable<T>
{
    public IDisposable Subscribe(IObserver<T> observer)
    {
        observer.OnCompleted();
        return new EmptyDisposable();
    }
}

internal sealed class EmptyDisposable : IDisposable
{
    public void Dispose() { }
}

/// <summary>
/// 同步捕获 IProgress 报告 (避免 <c>Progress&lt;T&gt;</c> 的 SynchronizationContext 异步时序问题)。
/// Report 调用直接入队, 无异步调度; 测试可在命令完成后立即读取 Reports 断言。
/// </summary>
internal sealed class CapturingProgress : IProgress<OperationProgress>
{
    private readonly ConcurrentQueue<OperationProgress> _reports = new();

    public void Report(OperationProgress value) => _reports.Enqueue(value);

    /// <summary>已捕获的进度快照 (ToArray 保证线程安全读取)。</summary>
    public IReadOnlyList<OperationProgress> Reports => _reports.ToArray();
}
