using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenShell.Commands;
using OpenShell.Operations;
using OpenShell.Providers;
using OpenShell.Security;

namespace OpenShell.Plugins;

/// <summary>
/// 默认 <see cref="IPluginLoader"/> 实现。Per ADR-0016.
/// 加载流程：创建 collectible ALC → 加载程序集 → 通过 <see cref="IPluginEntryPoint"/> 暴露 providers/commands
/// → 注册到 <see cref="IProviderRegistry"/> / <see cref="ICommandRegistry"/>。
/// 卸载流程 (完整版 <see cref="UnloadAsync"/>): 反注册 providers/commands → 等待 in-flight 操作归零 →
/// 调用 Shutdown → 卸载 ALC → 等 GC 回收。
/// 卸载流程 (简化版 <see cref="Unload"/>): 不等待 in-flight, 直接 Shutdown + 卸载 ALC。
/// 线程安全：使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 维护已加载列表。
/// </summary>
public sealed class PluginLoader : IPluginLoader
{
    private readonly IProviderRegistry _providers;
    private readonly ICommandRegistry _commands;
    private readonly IServiceProvider _services;
    private readonly IOperationTracker? _operationTracker;
    private readonly ILogger<PluginLoader>? _logger;
    private readonly ConcurrentDictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    // 入口点实例由插件 ALC 创建, 保留在 loader 私有状态中以便卸载时调用 Shutdown。
    // 不放入 LoadedPlugin 是为了避免外部持有 entry 引用阻止 ALC GC 回收。
    private readonly ConcurrentDictionary<string, IPluginEntryPoint> _entries = new(StringComparer.OrdinalIgnoreCase);

    // ADR-0036 §6: Provider 沙箱声明, 按 provider 名存储。第三方 Provider 加载时从 [assembly: ProviderAssembly]
    // 特性读取 SandboxLevel 并映射为 ProviderSandbox?; null 表示完全信任 (Full / None / 内置)。
    // 运行时由 SandboxContext.Current 传播, 供 §11 HTTP 拦截器与 §12 进程守卫读取。
    private readonly ConcurrentDictionary<string, ProviderSandbox?> _providerSandboxes = new(StringComparer.OrdinalIgnoreCase);

    public PluginLoader(
        IProviderRegistry providers,
        ICommandRegistry commands,
        IServiceProvider services,
        ILogger<PluginLoader>? logger = null,
        IOperationTracker? operationTracker = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger;
        _operationTracker = operationTracker;
    }

    /// <inheritdoc />
    public IReadOnlyList<LoadedPlugin> Loaded
        => _loaded.Values.ToList();

    /// <inheritdoc />
    public event EventHandler<LoadedPlugin>? PluginLoaded;

    /// <inheritdoc />
    public event EventHandler<LoadedPlugin>? PluginUnloaded;

    /// <inheritdoc />
    public LoadedPlugin Load(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // 重复加载同名插件先卸载旧的（热重载场景）。Per ADR-0016 §8.
        if (_loaded.ContainsKey(manifest.Name))
        {
            _logger?.LogWarning("Plugin '{Name}' is already loaded; unloading previous instance before reload.", manifest.Name);
            Unload(manifest.Name);
        }

        var assemblyPath = Path.GetFullPath(manifest.AssemblyPath);
        if (!File.Exists(assemblyPath))
            throw new PluginLoadException($"Plugin assembly not found: {assemblyPath}");

        // 1. 创建 collectible ALC。
        var context = new PluginCollectibleContext(assemblyPath, $"plugin::{manifest.Name}");

        Assembly assembly;
        IPluginEntryPoint entry;
        List<IProvider> providers;
        List<Type> commandTypes;
        // ADR-0036 §6: Provider 沙箱 (从 [assembly: ProviderAssembly] 特性映射); null = 完全信任。
        ProviderSandbox? sandbox = null;

        try
        {
            // 2. 加载程序集。
            assembly = context.LoadPluginAssembly(assemblyPath);

            // ADR-0036 §6: 读取 [assembly: ProviderAssembly] 特性的 Sandbox 级别, 映射为 ProviderSandbox?。
            // 缺失特性时降级为 null (完全信任), 不阻断加载 (§15 拒绝策略由上层 manifest 校验负责)。
            var sandboxAttr = assembly.GetCustomAttribute<ProviderAssemblyAttribute>();
            sandbox = sandboxAttr is not null
                ? SandboxLevelToProviderSandbox(sandboxAttr.Sandbox)
                : null;

            // 3. 解析入口类型。
            var entryType = assembly.GetType(manifest.EntryType);
            if (entryType is null)
                throw new PluginLoadException(
                    $"Entry type '{manifest.EntryType}' not found in assembly '{assembly.GetName().Name}'.");

            if (!typeof(IPluginEntryPoint).IsAssignableFrom(entryType))
                throw new PluginLoadException(
                    $"Entry type '{manifest.EntryType}' does not implement IPluginEntryPoint.");

            // 4. 实例化入口并初始化。
            // ADR-0036 §6: 进入 Provider 沙箱作用域, 使第三方 Provider 代码 (构造函数/Initialize/GetProviders/
            // GetCommandTypes) 内的网络 (§11) 与进程生成 (§12) 操作受沙箱约束。using 退出时恢复 previous 值。
            using (SandboxContext.EnterScope(sandbox))
            {
                entry = (IPluginEntryPoint)Activator.CreateInstance(entryType)!
                    ?? throw new PluginLoadException($"Failed to instantiate entry type '{manifest.EntryType}'.");

                entry.Initialize(_services);

                providers = (entry.GetProviders() ?? Array.Empty<IProvider>()).ToList();
                commandTypes = (entry.GetCommandTypes() ?? Array.Empty<Type>()).ToList();
            }
        }
        catch (PluginLoadException)
        {
            // 加载失败时释放 ALC，避免泄漏。Per ADR-0016 §5 (错误恢复)。
            try { context.Dispose(); } catch { /* best-effort */ }
            throw;
        }
        catch (Exception ex)
        {
            try { context.Dispose(); } catch { /* best-effort */ }
            throw new PluginLoadException(
                $"Failed to load plugin '{manifest.Name}': {ex.Message}", ex);
        }

        // 5. 注册 providers / commands。
        // 注册失败需回滚已注册项以保证原子性。
        var registeredProviders = new List<IProvider>();
        var registeredCommandCount = 0;
        try
        {
            foreach (var p in providers)
            {
                _providers.Register(p);
                registeredProviders.Add(p);
            }
            registeredCommandCount = _commands.RegisterTypes(commandTypes);
        }
        catch (Exception ex)
        {
            // 回滚已注册的 providers。
            foreach (var p in registeredProviders)
            {
                try { _providers.Unregister(p.Info.Name); } catch { /* best-effort */ }
            }
            // 回滚已注册的 commands。
            try { _commands.UnregisterTypes(commandTypes); } catch { /* best-effort */ }

            try { entry.Shutdown(); } catch { /* best-effort */ }
            try { context.Dispose(); } catch { /* best-effort */ }

            throw new PluginLoadException(
                $"Failed to register providers/commands for plugin '{manifest.Name}': {ex.Message}", ex);
        }

        var loaded = new LoadedPlugin
        {
            Name = manifest.Name,
            Version = manifest.Version,
            AssemblyPath = assemblyPath,
            Context = context,
            Providers = providers,
            CommandTypes = commandTypes,
            LoadedAt = DateTimeOffset.UtcNow,
        };

        // ADR-0036 §6: 按 provider 名存储沙箱声明, 供卸载路径 (ShutdownPluginEntry) 与外部消费者查询。
        // 同一插件的多个 provider 共享同一 assembly-level 沙箱。
        foreach (var p in providers)
        {
            _providerSandboxes[p.Info.Name] = sandbox;
        }

        _loaded[manifest.Name] = loaded;
        _entries[manifest.Name] = entry;

        _logger?.LogInformation(
            "Plugin '{Name}' v{Version} loaded: {ProviderCount} provider(s), {CommandCount} command(s).",
            manifest.Name, manifest.Version, providers.Count, registeredCommandCount);

        PluginLoaded?.Invoke(this, loaded);
        return loaded;
    }

    /// <inheritdoc />
    public bool Unload(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!_loaded.TryRemove(name, out var plugin)) return false;

        // 简化版: 不等待 in-flight 操作 (调用方需自行确保无活跃操作)。
        UnregisterPluginResources(plugin);
        // ADR-0036 §6: Shutdown 钩子在沙箱作用域内执行, 确保第三方清理代码受 §11/§12 约束。
        ShutdownPluginEntry(name, GetSandboxForPlugin(plugin));
        RemoveProviderSandboxes(plugin);
        DisposePluginContext(plugin);

        _logger?.LogInformation("Plugin '{Name}' unloaded (synchronous, no in-flight wait).", name);

        PluginUnloaded?.Invoke(this, plugin);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> UnloadAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!_loaded.TryRemove(name, out var plugin)) return false;

        // 1. 反注册 providers / commands (停止新调用)。Per ADR-0016 §3 第 1 步。
        UnregisterPluginResources(plugin);

        // 2. 等待所有 in-flight 操作完成 (带超时)。Per ADR-0016 §3 第 2 步。
        if (_operationTracker is not null)
        {
            foreach (var p in plugin.Providers)
            {
                var providerName = p.Info.Name;
                var inFlight = _operationTracker.GetInFlightCount(providerName);
                if (inFlight <= 0) continue;

                _logger?.LogInformation(
                    "Plugin '{Plugin}': waiting for {Count} in-flight operation(s) on provider '{Provider}' to complete.",
                    name, inFlight, providerName);

                var drained = await _operationTracker.WaitForProviderAsync(providerName, cancellationToken)
                    .ConfigureAwait(false);
                if (!drained)
                {
                    _logger?.LogWarning(
                        "Plugin '{Plugin}': wait for in-flight operations on provider '{Provider}' was cancelled or timed out; " +
                        "proceeding with unload anyway (operations may fail mid-flight).",
                        name, providerName);
                    // 不 return: 继续后续步骤, 因为 providers 已反注册, 插件已不可用。
                }
            }
        }

        // 3. 调用 Shutdown 钩子 (含 IAsyncDisposable 路径)。Per ADR-0016 §3 第 3 步。
        // ADR-0036 §6: Shutdown 钩子在沙箱作用域内执行, 确保第三方清理代码受 §11/§12 约束。
        await ShutdownPluginEntryAsync(name, GetSandboxForPlugin(plugin)).ConfigureAwait(false);
        RemoveProviderSandboxes(plugin);

        // 4. 卸载 ALC。Per ADR-0016 §3 第 4 步。
        var weakRef = new WeakReference(plugin.Context, trackResurrection: true);
        DisposePluginContext(plugin);

        // 5. 等 GC 回收 ALC。Per ADR-0016 §3 第 5 步: 最多 30 次 × 100ms。
        for (int i = 0; i < 30; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!weakRef.IsAlive) break;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        if (weakRef.IsAlive)
        {
            _logger?.LogWarning(
                "Plugin '{Name}': ALC was not garbage-collected after unload (possible lingering references).",
                name);
        }

        _logger?.LogInformation("Plugin '{Name}' unloaded (async, full drain).", name);

        PluginUnloaded?.Invoke(this, plugin);
        return true;
    }

    /// <summary>
    /// 反注册插件的 providers / commands。可重入 (Unregister 内部用 TryRemove)。
    /// 提取为方法以供同步/异步卸载路径共用。
    /// </summary>
    private void UnregisterPluginResources(LoadedPlugin plugin)
    {
        foreach (var p in plugin.Providers)
        {
            try { _providers.Unregister(p.Info.Name); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to unregister provider '{Name}' during plugin unload.", p.Info.Name); }
        }
        try { _commands.UnregisterTypes(plugin.CommandTypes); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to unregister commands for plugin '{Plugin}'.", plugin.Name); }
    }

    /// <summary>同步调用入口的 Shutdown 钩子 (简化版路径使用)。Per ADR-0036 §6: 在沙箱作用域内执行。</summary>
    private void ShutdownPluginEntry(string name, ProviderSandbox? sandbox)
    {
        if (!_entries.TryRemove(name, out var entry)) return;
        using (SandboxContext.EnterScope(sandbox))
        {
            try { entry.Shutdown(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Shutdown hook failed for plugin '{Name}'.", name); }
        }
    }

    /// <summary>
    /// 异步调用入口的 Shutdown 钩子 (完整版路径使用)。Per ADR-0016 §3 第 3 步。
    /// 若 entry 实现 <see cref="IAsyncDisposable"/>, 先 await DisposeAsync; 再调 Shutdown (幂等钩子)。
    /// Per ADR-0036 §6: 在沙箱作用域内执行, 确保第三方清理代码受 §11/§12 约束。
    /// </summary>
    private async ValueTask ShutdownPluginEntryAsync(string name, ProviderSandbox? sandbox)
    {
        if (!_entries.TryRemove(name, out var entry)) return;

        using (SandboxContext.EnterScope(sandbox))
        {
            // 优先 IAsyncDisposable (允许异步清理)。
            if (entry is IAsyncDisposable asyncDisp)
            {
                try { await asyncDisp.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "DisposeAsync failed for plugin '{Name}'.", name); }
            }

            try { entry.Shutdown(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Shutdown hook failed for plugin '{Name}'.", name); }
        }
    }

    /// <summary>卸载 ALC (调用 Context.Dispose 触发 ALC.Unload)。可重入。</summary>
    private void DisposePluginContext(LoadedPlugin plugin)
    {
        try { plugin.Context.Dispose(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to unload ALC for plugin '{Name}'.", plugin.Name); }
    }

    /// <inheritdoc />
    public bool TryGet(string name, out LoadedPlugin? plugin)
        => _loaded.TryGetValue(name, out plugin);

    // ---- ADR-0036 §6: Provider 沙箱辅助方法 ----

    /// <summary>
    /// 获取指定 Provider 的沙箱声明。Per ADR-0036 §6.
    /// <list type="bullet">
    ///   <item>返回 <c>false</c>: 该 Provider 非经 PluginLoader 加载 (内置 Provider), 无沙箱限制。</item>
    ///   <item>返回 <c>true</c> 且 <c>sandbox</c> 为 <c>null</c>: 声明 <c>SandboxLevel.Full</c> 的第三方 Provider, 等同无限制。</item>
    ///   <item>返回 <c>true</c> 且 <c>sandbox</c> 非空: 受限第三方 Provider, 需进入沙箱作用域。</item>
    /// </list>
    /// 外部消费者 (如操作引擎) 可在调用 Provider 方法前查询沙箱并通过 <see cref="SandboxContext.EnterScope"/> 设置作用域。
    /// </summary>
    public bool TryGetSandbox(string providerName, out ProviderSandbox? sandbox)
        => _providerSandboxes.TryGetValue(providerName, out sandbox);

    /// <summary>
    /// 查找插件对应的 Provider 沙箱 (取第一个 provider 的沙箱, 同一插件共享)。
    /// 用于卸载路径的 Shutdown 钩子作用域设置。
    /// </summary>
    private ProviderSandbox? GetSandboxForPlugin(LoadedPlugin plugin)
    {
        foreach (var p in plugin.Providers)
        {
            if (_providerSandboxes.TryGetValue(p.Info.Name, out var sandbox))
                return sandbox;
        }
        return null;
    }

    /// <summary>移除插件所有 provider 的沙箱声明 (卸载时调用)。</summary>
    private void RemoveProviderSandboxes(LoadedPlugin plugin)
    {
        foreach (var p in plugin.Providers)
        {
            _providerSandboxes.TryRemove(p.Info.Name, out _);
        }
    }

    /// <summary>
    /// 将 <see cref="SandboxLevel"/> 枚举映射为 <see cref="ProviderSandbox"/> 实例。
    /// Per ADR-0036 §6 pragmatic interpretation:
    /// <list type="bullet">
    ///   <item><c>None</c> / <c>Full</c> → <c>null</c> (完全信任, 等同内置 Provider, 无 §11/§12 强制)。</item>
    ///   <item><c>ReadOnly</c> / <c>Restricted</c> → 禁止网络 (<see cref="ProviderSandbox.NetworkAccess"/>=false) 与进程生成
    ///     (<see cref="ProviderSandbox.ProcessSpawn"/>=false)。路径级限制 (AllowedReadPaths/AllowedWritePaths) 为 advisory,
    ///     不做 OS 级拦截 (ALC hooking 复杂度过高); 沙箱元数据可用于审计。</item>
    /// </list>
    /// </summary>
    private static ProviderSandbox? SandboxLevelToProviderSandbox(SandboxLevel level) => level switch
    {
        SandboxLevel.None => null,
        SandboxLevel.Full => null,
        SandboxLevel.ReadOnly => _restrictedSandbox,
        SandboxLevel.Restricted => _restrictedSandbox,
        _ => null,
    };

    /// <summary>ReadOnly / Restricted 共用的受限沙箱实例 (不可变, 共享安全)。</summary>
    private static readonly ProviderSandbox _restrictedSandbox = new(
        AllowedReadPaths: new HashSet<string>(0, StringComparer.OrdinalIgnoreCase),
        AllowedWritePaths: new HashSet<string>(0, StringComparer.OrdinalIgnoreCase),
        NetworkAccess: false,
        ProcessSpawn: false);
}
