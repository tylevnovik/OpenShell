using Microsoft.Extensions.Logging;
using OpenShell.Providers;

namespace OpenShell.Packaging.Installation;

/// <summary>
/// 主机升级后的 Provider 兼容性巡检器。Per ADR-0039 §9 / ADR-0038 §2.
/// 在 OpenShell 主程序更新完成后调用: 遍历所有已安装 Provider, 重新校验其
/// <see cref="ProviderManifest.RequiredApiVersion"/> 是否仍与当前主机 API 兼容。
/// 不兼容时尝试自动升级 Provider; 升级失败或无兼容版本则在 <see cref="PluginsConfig"/> 中
/// 标记为 <c>Enabled=false</c>, 避免加载失败阻塞主机启动。
/// </summary>
/// <remarks>
/// <see cref="ApiCompatibilityChecker"/> 为静态类 (无法注入), 直接静态调用。
/// 本类型本身不注册到 DI (由调用方按需 new); 仅依赖 <see cref="IServiceProvider"/> 解析
/// <see cref="IProviderInstaller"/> / <see cref="PluginsConfig"/> / <see cref="ILogger{TCategoryName}"/>。
/// </remarks>
public sealed class PostUpdateCompatibilityChecker
{
    private readonly ILogger<PostUpdateCompatibilityChecker>? _logger;

    /// <summary>构造一个巡检器。logger 可选 (CLI 静默运行时传 null)。</summary>
    public PostUpdateCompatibilityChecker(ILogger<PostUpdateCompatibilityChecker>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 执行一次兼容性巡检。Per ADR-0039 §9.
    /// 流程:
    /// <list type="number">
    ///   <item>从 <paramref name="services"/> 解析 <see cref="IProviderInstaller"/> 与 <see cref="PluginsConfig"/>。</item>
    ///   <item>枚举所有已安装 Provider (<see cref="IProviderInstaller.ListInstalled"/>)。</item>
    ///   <item>对每个 Provider: 从安装目录读取 <c>openshell.provider.json</c>, 调用 <see cref="ApiCompatibilityChecker.Verify"/>。</item>
    ///   <item>校验失败 (<see cref="ApiMismatchException"/>) 时尝试 <see cref="IProviderInstaller.UpdateAsync"/> 自动升级。</item>
    ///   <item>升级失败或仍无兼容版本: 在 <see cref="PluginsConfig"/> 中标记 <c>Enabled=false</c> 并持久化。</item>
    /// </list>
    /// 单个 Provider 处理异常不会中断整体巡检; 返回的 <see cref="CompatibilityReport"/> 含每项结果。
    /// </summary>
    public async Task<CompatibilityReport> CheckAfterUpdateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var installer = services.GetService(typeof(IProviderInstaller)) as IProviderInstaller
            ?? throw new InvalidOperationException(
                "IProviderInstaller is not registered in the service provider.");
        var pluginsConfig = services.GetService(typeof(PluginsConfig)) as PluginsConfig
            ?? new PluginsConfig();

        // 加载已持久化的 plugins.config.toml (含 enabled 状态)。
        await pluginsConfig.LoadAsync(ct).ConfigureAwait(false);

        var installed = installer.ListInstalled();
        var results = new List<ProviderCheckResult>(installed.Count);
        var updated = new List<string>();
        var disabled = new List<string>();
        var ok = new List<string>();

        foreach (var prov in installed)
        {
            ct.ThrowIfCancellationRequested();
            var name = prov.Name;
            try
            {
                var manifest = await ReadInstalledManifestAsync(prov, ct).ConfigureAwait(false);
                // 静态校验: 抛 ApiMismatchException 表示不兼容。
                ApiCompatibilityChecker.Verify(manifest.ToProviderInfo());
                ok.Add(name);
                results.Add(new ProviderCheckResult(name, prov.CurrentVersion, CheckOutcome.Compatible, null));
            }
            catch (ApiMismatchException ex)
            {
                _logger?.LogWarning(
                    "Provider '{Name}' incompatible after host update (requires API {Required}, host provides {Host}); attempting auto-update.",
                    name, ex.RequiredApiVersion, ex.HostApiVersion);

                // 尝试自动升级到与当前主机兼容的版本。
                var (outcome, detail) = await TryAutoUpdateAsync(installer, name, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case UpdateOutcome.Updated:
                        updated.Add(name);
                        results.Add(new ProviderCheckResult(name, prov.CurrentVersion, CheckOutcome.Updated, detail));
                        break;
                    case UpdateOutcome.NoCompatibleVersion:
                    case UpdateOutcome.UpdateFailed:
                        // 升级失败: 标记为禁用, 避免主机启动时加载失败。
                        DisableProvider(pluginsConfig, name);
                        disabled.Add(name);
                        results.Add(new ProviderCheckResult(name, prov.CurrentVersion, CheckOutcome.Disabled, detail ?? ex.Remediation));
                        break;
                    default:
                        DisableProvider(pluginsConfig, name);
                        disabled.Add(name);
                        results.Add(new ProviderCheckResult(name, prov.CurrentVersion, CheckOutcome.Disabled, detail));
                        break;
                }
            }
            catch (Exception ex)
            {
                // manifest 缺失 / 解析失败等: 保守起见也禁用, 避免加载未通过校验的 provider。
                _logger?.LogWarning(ex, "Failed to verify provider '{Name}'; marking as disabled.", name);
                DisableProvider(pluginsConfig, name);
                disabled.Add(name);
                results.Add(new ProviderCheckResult(name, prov.CurrentVersion, CheckOutcome.Disabled, ex.Message));
            }
        }

        // 持久化 plugins.config.toml (即便没有变更也写一次, 保证 enabled 状态一致)。
        try
        {
            await pluginsConfig.SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist plugins.config.toml after compatibility check.");
        }

        _logger?.LogInformation(
            "Post-update compatibility check: {Ok} ok, {Updated} updated, {Disabled} disabled (total {Total}).",
            ok.Count, updated.Count, disabled.Count, installed.Count);

        return new CompatibilityReport(results, ok, updated, disabled);
    }

    /// <summary>
    /// 从已安装目录读取 <c>openshell.provider.json</c>。Per ADR-0039 §2.
    /// 优先读 <c>current/</c> (符号链接或目录复制), 失败时 fallback 到 <c>{CurrentVersion}/</c>。
    /// </summary>
    private static async Task<ProviderManifest> ReadInstalledManifestAsync(InstalledProvider prov, CancellationToken ct)
    {
        var candidates = new List<string>(2);
        if (!string.IsNullOrEmpty(prov.CurrentVersion))
        {
            // current 子目录 (符号链接 / junction / 目录复制 fallback 均可读)。
            candidates.Add(Path.Combine(prov.InstallRoot, "current", OspPackage.ManifestEntryName));
            // 直接版本目录。
            candidates.Add(Path.Combine(prov.InstallRoot, prov.CurrentVersion, OspPackage.ManifestEntryName));
        }
        // 兜底: 扫描 InstallRoot 下任意版本子目录。
        if (Directory.Exists(prov.InstallRoot))
        {
            foreach (var sub in Directory.EnumerateDirectories(prov.InstallRoot))
            {
                var ver = Path.GetFileName(sub);
                if (string.Equals(ver, "current", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(Path.Combine(sub, OspPackage.ManifestEntryName));
            }
        }

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return ProviderManifest.Parse(json);
        }

        throw new OspPackageException(
            $"Provider '{prov.Name}' has no '{OspPackage.ManifestEntryName}' in its install directory '{prov.InstallRoot}'.");
    }

    /// <summary>
    /// 尝试升级 Provider 到与当前主机兼容的版本。Per ADR-0039 §9.
    /// 成功后再次校验兼容性 (防止注册源返回的"最新版"仍不兼容)。
    /// </summary>
    /// <returns>(结果, 说明文本)。Updated 表示升级后兼容; NoCompatibleVersion 表示无可用更新;
    /// UpdateFailed 表示升级过程抛异常。</returns>
    private async Task<(UpdateOutcome Outcome, string? Detail)> TryAutoUpdateAsync(
        IProviderInstaller installer, string name, CancellationToken ct)
    {
        try
        {
            var result = await installer.UpdateAsync(name, ct).ConfigureAwait(false);
            // 升级后再次校验: 防止注册源最新版仍不兼容 (例如 provider 已弃用维护)。
            var updated = installer.ListInstalled()
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (updated is null || string.IsNullOrEmpty(updated.CurrentVersion))
            {
                return (UpdateOutcome.NoCompatibleVersion, $"Update reported success for '{name}' but no installed version found.");
            }
            var manifest = await ReadInstalledManifestAsync(updated, ct).ConfigureAwait(false);
            try
            {
                ApiCompatibilityChecker.Verify(manifest.ToProviderInfo());
                return (UpdateOutcome.Updated, $"Updated to v{result.Version}.");
            }
            catch (ApiMismatchException ex)
            {
                return (UpdateOutcome.NoCompatibleVersion,
                    $"Latest available v{result.Version} still incompatible: {ex.Remediation}");
            }
        }
        catch (OspPackageException ex)
        {
            return (UpdateOutcome.NoCompatibleVersion, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Auto-update failed for provider '{Name}'.", name);
            return (UpdateOutcome.UpdateFailed, ex.Message);
        }
    }

    /// <summary>在 <see cref="PluginsConfig"/> 中把指定 Provider 标记为禁用 (Enabled=false), 保留其余字段。</summary>
    private static void DisableProvider(PluginsConfig config, string name)
    {
        var existing = config.TryGet(name);
        config.Upsert(new ProviderEntry
        {
            Name = name,
            Enabled = false,
            LoadOrder = existing?.LoadOrder ?? 100,
            AutoUpdate = existing?.AutoUpdate ?? false,
            Config = existing?.Config ?? new Dictionary<string, object?>(),
        });
    }
}

/// <summary>单个 Provider 的巡检结果。</summary>
public sealed record ProviderCheckResult(
    string Name,
    string? InstalledVersion,
    CheckOutcome Outcome,
    string? Detail);

/// <summary>巡检结果分类。</summary>
public enum CheckOutcome
{
    /// <summary>兼容, 无需操作。</summary>
    Compatible,
    /// <summary>原不兼容, 已成功升级到兼容版本。</summary>
    Updated,
    /// <summary>不兼容且无法自动修复, 已在 plugins.config.toml 中禁用。</summary>
    Disabled,
}

/// <summary>自动升级尝试结果。</summary>
internal enum UpdateOutcome
{
    Updated,
    NoCompatibleVersion,
    UpdateFailed,
}

/// <summary>整次巡检的汇总报告。</summary>
public sealed record CompatibilityReport(
    IReadOnlyList<ProviderCheckResult> Results,
    IReadOnlyList<string> Compatible,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Disabled)
{
    /// <summary>本次巡检覆盖的 Provider 总数。</summary>
    public int Total => Results.Count;
}
