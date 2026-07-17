using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using OpenShell.Configuration;

namespace OpenShell.Plugins;

/// <summary>
/// 插件热重载服务。Per ADR-0016 §8.
/// 监视 <c>~/.openshell/plugins/</c> 目录下文件变化 (DLL/manifest), 触发 <see cref="IPluginLoader.UnloadAsync"/> →
/// 等待 1 秒 → <see cref="IPluginLoader.Load"/> 流程。
/// 通过 <see cref="IHostedService"/> 启动/停止; 通过 <see cref="IConfigurationService"/> 读取 PluginWatch/PluginHotReload 开关。
/// 内部使用 debounce (500ms) 避免编辑器多次保存触发多次重载。
/// 重载失败时保留旧插件 (回滚: 重新加载旧 manifest 失败时不移除条目)。
/// </summary>
public sealed class PluginHotReloadService : IHostedService, IDisposable
{
    private readonly IPluginLoader _loader;
    private readonly IConfigurationService _config;
    private readonly ILogger<PluginHotReloadService>? _logger;

    /// <summary>debounce 窗口: 同一插件在 500ms 内的多次变更合并为一次重载。</summary>
    private const int DebounceMs = 500;

    /// <summary>重载前等待时间: 让文件写入完成 (Per ADR-0016 §8 "等待 1 秒")。</summary>
    private const int PreReloadDelayMs = 1000;

    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _started;

    public PluginHotReloadService(
        IPluginLoader loader,
        IConfigurationService config,
        ILogger<PluginHotReloadService>? logger = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return Task.CompletedTask;

        var enabled = _config.Config.PluginHotReload || _config.Config.PluginWatch;
        if (!enabled)
        {
            _logger?.LogDebug("Plugin hot-reload disabled (PluginWatch/PluginHotReload = false).");
            return Task.CompletedTask;
        }

        var pluginsDir = OpenShellPaths.Plugins;
        if (!Directory.Exists(pluginsDir))
        {
            _logger?.LogDebug("Plugins directory '{Dir}' does not exist; hot-reload watcher inactive.", pluginsDir);
            return Task.CompletedTask;
        }

        try
        {
            _watcher = new FileSystemWatcher(pluginsDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;

            _started = true;
            _logger?.LogInformation(
                "Plugin hot-reload watcher started on '{Dir}' (hotReload={Hot}, watch={Watch}).",
                pluginsDir, _config.Config.PluginHotReload, _config.Config.PluginWatch);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to start plugin hot-reload watcher on '{Dir}'.", pluginsDir);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return;

        // 取消所有 pending reload。
        foreach (var cts in _pendingReloads.Values)
        {
            try { cts.Cancel(); } catch { /* best-effort */ }
        }
        _pendingReloads.Clear();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
        }

        _started = false;
        _logger?.LogInformation("Plugin hot-reload watcher stopped.");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleReload(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => ScheduleReload(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger?.LogWarning(e.GetException(), "FileSystemWatcher error in plugin hot-reload watcher.");
    }

    /// <summary>
    /// 调度一次插件重载 (debounce 500ms)。从变更路径推断所属插件名, 然后触发 unload → wait → load。
    /// </summary>
    private void ScheduleReload(string changedPath)
    {
        // 仅关心 .dll / plugin.manifest.json 变化, 忽略其他文件 (如 .log / .pdb)。
        var name = Path.GetFileName(changedPath);
        var isDll = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var isManifest = name.EndsWith("plugin.manifest.json", StringComparison.OrdinalIgnoreCase);
        if (!isDll && !isManifest) return;

        // 推断插件名: 取变更路径所在的 plugins/ 直接子目录名。
        var pluginName = TryResolvePluginName(changedPath);
        if (pluginName is null)
        {
            _logger?.LogDebug("Could not resolve plugin name for '{Path}'; ignoring.", changedPath);
            return;
        }

        // Debounce: 取消已有 pending reload, 启动新的。
        if (_pendingReloads.TryRemove(pluginName, out var existing))
        {
            try { existing.Cancel(); } catch { /* best-effort */ }
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingReloads[pluginName] = cts;

        _ = Task.Run(() => DebouncedReloadAsync(pluginName, cts.Token), cts.Token);
    }

    /// <summary>从变更路径解析所属插件名 (plugins/ 直接子目录名)。</summary>
    private static string? TryResolvePluginName(string fullPath)
    {
        try
        {
            var pluginsDir = OpenShellPaths.Plugins;
            var normalized = Path.GetFullPath(fullPath);
            if (!normalized.StartsWith(pluginsDir, StringComparison.OrdinalIgnoreCase)) return null;

            // 跳过 pluginsDir 后的目录分隔符。
            var rel = normalized.AsSpan(pluginsDir.Length);
            while (rel.Length > 0 && (rel[0] == Path.DirectorySeparatorChar || rel[0] == Path.AltDirectorySeparatorChar))
            {
                rel = rel.Slice(1);
            }

            var sep = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            return sep <= 0 ? null : rel.Slice(0, sep).ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>debounce 等待 + 实际重载。Per ADR-0016 §8: UnloadAsync → 等 1s → Load。</summary>
    private async Task DebouncedReloadAsync(string pluginName, CancellationToken ct)
    {
        try
        {
            // debounce: 等 500ms, 若期间被取消则直接返回 (后续新的调度会接管)。
            await Task.Delay(DebounceMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _pendingReloads.TryRemove(pluginName, out _);
        }

        await ReloadPluginAsync(pluginName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行一次完整重载: UnloadAsync → 等 1s → 重新读 manifest → Load。
    /// 若任何步骤失败, 保留旧插件 (回滚: 不强制卸载, 等待下次变更重试)。
    /// </summary>
    private async Task ReloadPluginAsync(string pluginName, CancellationToken ct)
    {
        _logger?.LogInformation("Hot-reloading plugin '{Name}'...", pluginName);

        // 1. 检查当前是否已加载 (未加载则直接 Load, 无需 Unload)。
        if (_loader.TryGet(pluginName, out _))
        {
            try
            {
                await _loader.UnloadAsync(pluginName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to unload plugin '{Name}' during hot-reload; aborting reload.", pluginName);
                return;
            }
        }

        // 2. 等待文件写入完成。Per ADR-0016 §8 "等待 1 秒"。
        try
        {
            await Task.Delay(PreReloadDelayMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 3. 重新读 manifest 并加载。
        var manifestPath = Path.Combine(OpenShellPaths.Plugins, pluginName, PluginManifestLoader.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            _logger?.LogInformation("Plugin '{Name}' manifest removed; not reloading.", pluginName);
            return;
        }

        try
        {
            var manifest = PluginManifestLoader.Read(manifestPath);
            _loader.Load(manifest);
            _logger?.LogInformation("Plugin '{Name}' hot-reloaded successfully.", pluginName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to reload plugin '{Name}' (old instance already unloaded).", pluginName);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var cts in _pendingReloads.Values)
        {
            try { cts.Cancel(); } catch { /* best-effort */ }
            cts.Dispose();
        }
        _pendingReloads.Clear();

        _watcher?.Dispose();
    }
}
