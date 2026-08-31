using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenShell.Packaging.Registry;
using OpenShell.Packaging.Signing;
using OpenShell.Plugins;
using OpenShell.Providers;

namespace OpenShell.Packaging.Installation;

/// <summary>
/// 默认 <see cref="IProviderInstaller"/> 实现。Per ADR-0039 §6 / §7.
/// 协调 <see cref="RegistryClient"/> (下载) / <see cref="ISignatureVerifier"/> (验签) /
/// <see cref="DependencyResolver"/> (拓扑/范围) / <see cref="PluginsConfig"/> (启用记录) /
/// <see cref="IPluginLoader"/> (卸载时反注册插件)。
/// 安装目录: <see cref="OpenShellPaths.ProvidersDir"/>/{name}/{version}/, current 符号链接指向最新版。
/// </summary>
public sealed class ProviderInstaller : IProviderInstaller
{
    private readonly ProviderSourceRegistry _sources;
    private readonly RegistryClient _client;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly DependencyResolver _resolver;
    private readonly PluginsConfig _pluginsConfig;
    private readonly ILogger<ProviderInstaller>? _logger;
    private readonly string _providersDir;
    private readonly IPluginLoader? _pluginLoader;

    public ProviderInstaller(
        ProviderSourceRegistry sources,
        RegistryClient client,
        ISignatureVerifier signatureVerifier,
        PluginsConfig pluginsConfig,
        ILogger<ProviderInstaller>? logger = null,
        string? providersDir = null,
        // ADR-0039 §6 / §11: 卸载时需先反注册 PluginLoader 中已加载的 provider 实例, 释放 ALC。
        // 可选参数: 缺省 null 时跳过 unload (向后兼容现有 DI 注册)。
        // TODO: wire IPluginLoader from DI in Program.cs (当前 Program.cs 未传入, 卸载时跳过 unload)。
        IPluginLoader? pluginLoader = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _pluginsConfig = pluginsConfig ?? throw new ArgumentNullException(nameof(pluginsConfig));
        _logger = logger;
        _resolver = new DependencyResolver();
        _providersDir = providersDir ?? OpenShellPaths.ProvidersDir;
        _pluginLoader = pluginLoader;
    }

    /// <inheritdoc />
    public async Task<InstallResult> InstallAsync(
        string name,
        string? version = null,
        string? sourceName = null,
        bool dryRun = false,
        byte[]? trustKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Provider name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(version) == false)
            ValidatePackageVersionSegment(version!);

        OpenShellPaths.EnsurePackagingDirs();

        // 1) 选源: 按优先级遍历所有源 (或仅指定源), 找到第一个返回该包的源。
        var sources = _sources.Sources;
        if (!string.IsNullOrEmpty(sourceName))
        {
            var src = sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new OspPackageException($"Source '{sourceName}' is not registered.");
            sources = new[] { src };
        }
        if (sources.Count == 0)
            throw new OspPackageException("No provider source is registered. Use Register-ProviderSource to add one.");

        PackageInfo? info = null;
        ProviderSource? matchedSource = null;
        foreach (var s in sources)
        {
            try
            {
                info = await _client.GetPackageAsync(s, name, cancellationToken).ConfigureAwait(false);
                if (info is not null) { matchedSource = s; break; }
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Source '{Source}' query for '{Name}' failed.", s.Name, name); }
        }
        if (info is null || matchedSource is null)
            throw new OspPackageException($"Provider '{name}' not found in any registered source.");

        // 2) 选版本: 指定 → latest → 最新稳定版。
        string resolvedVersion;
        if (!string.IsNullOrEmpty(version))
            resolvedVersion = version!;
        else if (!string.IsNullOrEmpty(info.Latest))
            resolvedVersion = info.Latest!;
        else
        {
            // 从 versions 选最新非 deprecated 的稳定版。
            var cand = info.Versions
                .Where(v => !v.Deprecated)
                .OrderByDescending(v => TryParseVersion(v.Version))
                .FirstOrDefault();
            resolvedVersion = cand?.Version ?? throw new OspPackageException($"Provider '{name}' has no available versions.");
        }

        // 3) 解析依赖 (dry-run 也执行此步)。
        var installed = ListInstalled().ToDictionary(p => p.Name, p => p.CurrentVersion ?? "", StringComparer.OrdinalIgnoreCase);
        // 当前包若已安装, 加入 installed 字典供后续包引用。
        if (installed.TryGetValue(name, out var existingCur))
        {
            // 已安装某版本, 但本次要装新版本: 暂用 resolvedVersion 供依赖解析。
            installed[name] = resolvedVersion;
        }
        else
        {
            installed[name] = resolvedVersion;
        }

        // ADR-0039 §7: dry-run 时通过 GET /v1/packages/{name}/{version} 获取完整 manifest (含依赖列表),
        // 供依赖解析器在不下载 .osp 的前提下产出依赖图。真实安装流程则从 .osp 包内读取 manifest (步骤 7)。
        // 获取失败 (源不支持该端点 / 网络错误) 时降级为空依赖列表, 不阻断 dry-run。
        IReadOnlyList<ResolvedDependency> deps = Array.Empty<ResolvedDependency>();
        if (dryRun)
        {
            try
            {
                var versionManifest = await _client.GetVersionManifestAsync(matchedSource, name, resolvedVersion, cancellationToken).ConfigureAwait(false);
                if (versionManifest is not null)
                {
                    deps = _resolver.Resolve(versionManifest, installed);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Dry-run dependency resolution failed for '{Name}' v{Version}; dependencies will be empty.", name, resolvedVersion);
            }
        }

        // 4) dry-run: 至此返回, 不下载不写盘。
        if (dryRun)
        {
            return new InstallResult
            {
                Name = name,
                Version = resolvedVersion,
                Source = matchedSource.Name,
                DryRun = true,
                Dependencies = deps,
                Summary = $"Dry-run: would install '{name}' v{resolvedVersion} from '{matchedSource.Name}'.",
            };
        }

        // 5) 下载 .osp 到缓存目录。
        var cachePath = Path.Combine(OpenShellPaths.ProviderCacheDir, $"{name}-{resolvedVersion}.osp");
        await _client.DownloadPackageAsync(matchedSource, name, resolvedVersion, cachePath, cancellationToken).ConfigureAwait(false);

        // 6) 验签。
        await using var pkg = await OspPackage.OpenAsync(cachePath, cancellationToken).ConfigureAwait(false);
        var manifest = await pkg.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(manifest.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new OspPackageException(
                $"Package manifest name '{manifest.Name}' does not match requested provider '{name}'.");
        if (!string.Equals(manifest.Version, resolvedVersion, StringComparison.OrdinalIgnoreCase))
            throw new OspPackageException(
                $"Package manifest version '{manifest.Version}' does not match requested version '{resolvedVersion}'.");
        var (sig, pub) = pkg.ReadSignature();
        var payloadHash = await pkg.ComputePayloadHashAsync(manifest, cancellationToken).ConfigureAwait(false);
        var sigResult = await _signatureVerifier.VerifyAsync(manifest, payloadHash, pub, sig, matchedSource.Trusted, cancellationToken).ConfigureAwait(false);
        if (sigResult == SignatureResult.Invalid)
            throw new OspPackageException($"Signature verification failed for '{name}' v{resolvedVersion}'.");
        if (sigResult == SignatureResult.Untrusted)
        {
            // ADR-0039 §6 / §9: 用户通过 -TrustKey 显式信任包内嵌公钥时, 放宽校验。
            // 仅当包内嵌了公钥且与用户提供的 trustKey 逐字节相等 (恒定时间比较防时序攻击) 时通过。
            if (trustKey is null || pub is null || !CryptographicOperations.FixedTimeEquals(trustKey, pub))
            {
                throw new OspPackageException(
                    $"Package '{name}' is unsigned or signed with an untrusted key. Use Install-Provider -TrustKey <pubkey> to trust.");
            }
            _logger?.LogWarning(
                "Package '{Name}' v{Version} trusted via explicit -TrustKey (not via source trust).", name, resolvedVersion);
        }

        // 7) 重新用真实 manifest 解析依赖。
        deps = _resolver.Resolve(manifest, installed);
        var unsatisfied = deps.Where(d => string.Equals(d.Kind, "provider", StringComparison.OrdinalIgnoreCase) && !d.Satisfied).ToList();
        if (unsatisfied.Count > 0)
        {
            // ADR-0039 §7: 递归安装被依赖 provider (深度优先) + 事务回滚。
            // 记录每个被装上的依赖的 (name, previousVersion): previousVersion=null 表示全新安装 (回滚时 Uninstall),
            // previousVersion 非 null 表示升级 (回滚时 InstallAsync 恢复旧版本)。
            // 注意: 仅记录成功安装的依赖 (InstallAsync 抛异常时该条不计入, 避免回滚未就位的包)。
            var preInstallSnapshot = ListInstalled();
            var previousVersions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in unsatisfied)
            {
                var prev = preInstallSnapshot.FirstOrDefault(p => string.Equals(p.Name, u.Name, StringComparison.OrdinalIgnoreCase));
                previousVersions[u.Name] = prev?.CurrentVersion;
            }

            var installedForRollback = new List<string>(unsatisfied.Count);
            try
            {
                foreach (var u in unsatisfied)
                {
                    _logger?.LogInformation("Installing missing dependency '{Dep}' {Range}.", u.Name, u.RequestedVersion);
                    // 依赖包不继承顶层 trustKey: 用户仅显式信任了顶层包的公钥, 依赖包须独立满足签名校验。
                    await InstallAsync(u.Name, null, matchedSource.Name, false, null, cancellationToken).ConfigureAwait(false);
                    installedForRollback.Add(u.Name);
                }
            }
            catch (Exception ex)
            {
                // 事务回滚: 把已成功安装的依赖恢复到安装前状态。
                _logger?.LogError(ex, "Dependency installation failed for '{Name}'; rolling back {Count} provider(s).", name, installedForRollback.Count);
                foreach (var depName in installedForRollback)
                {
                    var prevVer = previousVersions.TryGetValue(depName, out var pv) ? pv : null;
                    try
                    {
                        if (prevVer is null)
                        {
                            // 全新安装的依赖 → 卸载。
                            await UninstallAsync(depName, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // 升级的依赖 → 恢复到之前的版本。
                            _logger?.LogInformation("Rolling back '{Dep}' to v{Version}.", depName, prevVer);
                            await InstallAsync(depName, prevVer, matchedSource.Name, false, null, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        // 单个回滚失败不阻断其他回滚; 最终仍 re-throw 原始异常。
                        _logger?.LogWarning(rollbackEx, "Rollback failed for '{Dep}'.", depName);
                    }
                }
                throw;
            }
        }

        // 8) API 兼容性校验 (ADR-0038)。
        ApiCompatibilityChecker.Verify(manifest.ToProviderInfo());

        // 9-11) 先解压到 staging, 再一次性切换版本/current/config。
        // 任一步失败都恢复旧目录、current 和配置, 避免“安装成功但启动找不到插件”。
        ValidatePackageVersionSegment(resolvedVersion);
        var existing = _pluginsConfig.TryGet(name);
        var installDir = Path.Combine(_providersDir, SanitiseDir(name), resolvedVersion);
        var currentPath = await InstallVersionTransactionalAsync(
            name, resolvedVersion, pkg, existing, cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation("Installed provider '{Name}' v{Version} to {Path}.", name, resolvedVersion, installDir);

        return new InstallResult
        {
            Name = name,
            Version = resolvedVersion,
            InstallPath = installDir,
            CurrentPath = currentPath,
            Source = matchedSource.Name,
            DryRun = false,
            Dependencies = deps,
            Summary = $"Installed '{name}' v{resolvedVersion} from '{matchedSource.Name}'.",
        };
    }

    private async Task<string> InstallVersionTransactionalAsync(
        string name,
        string version,
        OspPackage package,
        ProviderEntry? existing,
        CancellationToken ct)
    {
        var providerRoot = Path.Combine(_providersDir, SanitiseDir(name));
        var installDir = Path.Combine(providerRoot, version);
        var currentPath = Path.Combine(providerRoot, "current");
        var stagingDir = Path.Combine(providerRoot, $".staging-{Guid.NewGuid():N}");
        var installBackup = Path.Combine(providerRoot, $".backup-{version}-{Guid.NewGuid():N}");
        var currentBackup = Path.Combine(providerRoot, $".current-backup-{Guid.NewGuid():N}");
        var hadInstall = PathExists(installDir);
        var hadCurrent = PathExists(currentPath);
        var configChanged = false;
        var committed = false;
        var installActivated = false;
        var currentMoved = false;
        var currentActivated = false;

        try
        {
            Directory.CreateDirectory(providerRoot);
            await package.ExtractToAsync(stagingDir, ct).ConfigureAwait(false);

            if (hadInstall)
                MovePath(installDir, installBackup);
            MovePath(stagingDir, installDir);
            installActivated = true;

            if (hadCurrent)
            {
                MovePath(currentPath, currentBackup);
                currentMoved = true;
            }

            await UpdateCurrentLinkAsync(name, version, ct).ConfigureAwait(false);
            if (!PathExists(currentPath))
                throw new IOException($"Failed to create current provider link at '{currentPath}'.");
            currentActivated = true;

            _pluginsConfig.Upsert(new ProviderEntry
            {
                Name = name,
                Enabled = existing?.Enabled ?? true,
                LoadOrder = existing?.LoadOrder ?? 100,
                AutoUpdate = existing?.AutoUpdate ?? false,
                Config = existing?.Config ?? new Dictionary<string, object?>(),
            });
            configChanged = true;
            await _pluginsConfig.SaveAsync(ct).ConfigureAwait(false);

            committed = true;
            try { DeletePath(installBackup); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to remove install backup '{Path}'.", installBackup); }
            try { DeletePath(currentBackup); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to remove current backup '{Path}'.", currentBackup); }
            return currentPath;
        }
        catch
        {
            // 恢复配置时只触碰当前 provider, 不覆盖其他 provider 的并发更新。
            if (configChanged)
            {
                try
                {
                    if (existing is null) _pluginsConfig.Remove(name);
                    else _pluginsConfig.Upsert(existing);
                    await _pluginsConfig.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _logger?.LogError(rollbackEx, "Failed to roll back plugins.config for '{Name}'.", name);
                }
            }

            if (installActivated)
            {
                try { DeletePath(installDir); } catch { /* best-effort rollback */ }
            }
            if (currentActivated && PathExists(currentPath))
            {
                try { DeletePath(currentPath); } catch { /* best-effort rollback */ }
            }
            if (currentMoved && PathExists(currentBackup))
            {
                try { MovePath(currentBackup, currentPath); } catch (Exception ex) { _logger?.LogError(ex, "Failed to restore current link for '{Name}'.", name); }
            }

            if (PathExists(installBackup))
            {
                try { MovePath(installBackup, installDir); } catch (Exception ex) { _logger?.LogError(ex, "Failed to restore installed version for '{Name}'.", name); }
            }

            try { DeletePath(stagingDir); } catch { /* best-effort rollback */ }
            throw;
        }
        finally
        {
            // 成功路径已清理; 失败路径若恢复失败也不应留下可被误识别为版本的 staging。
            try { DeletePath(stagingDir); } catch { }
            if (committed)
            {
                try { DeletePath(installBackup); } catch { }
                try { DeletePath(currentBackup); } catch { }
            }
        }
    }

    /// <inheritdoc />
    public async Task<InstallResult> UpdateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Provider name is required.", nameof(name));

        var installed = ListInstalled().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new OspPackageException($"Provider '{name}' is not installed.");

        // 查询最新版。
        var sources = _sources.Sources;
        string? latestVersion = null;
        ProviderSource? matchedSource = null;
        foreach (var s in sources)
        {
            try
            {
                var latest = await _client.GetLatestAsync(s, name, cancellationToken).ConfigureAwait(false);
                if (latest is not null) { latestVersion = latest.Version; matchedSource = s; break; }
            }
            catch { /* try next */ }
        }
        if (latestVersion is null || matchedSource is null)
            throw new OspPackageException($"No update available for '{name}' (no source returned a latest version).");

        if (installed.CurrentVersion is not null
            && TryParseVersion(latestVersion) <= TryParseVersion(installed.CurrentVersion))
        {
            return new InstallResult
            {
                Name = name,
                Version = installed.CurrentVersion,
                Source = matchedSource.Name,
                Summary = $"'{name}' is already up-to-date (v{installed.CurrentVersion}).",
            };
        }

        // 调用 InstallAsync 安装新版本 (不卸载旧版, 由 current 切换)。Update 不继承 trustKey。
        return await InstallAsync(name, latestVersion, matchedSource.Name, false, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UninstallAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var installed = ListInstalled().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (installed is null) return false;

        // 0) ADR-0039 §6 / §11: 先反注册 PluginLoader 中已加载的 provider 实例, 释放 ALC 与已注册命令。
        // 必须在删除文件之前完成: ALC 卸载需要读取程序集文件, 删除后无法回收。
        // 若 provider 未加载 (TryGet 返回 false), UnloadAsync 也会返回 false, 视为 no-op。
        if (_pluginLoader is not null)
        {
            try
            {
                var unloaded = await _pluginLoader.UnloadAsync(name, cancellationToken).ConfigureAwait(false);
                if (unloaded)
                {
                    _logger?.LogInformation("Plugin '{Name}' unloaded from PluginLoader before uninstall.", name);
                }
            }
            catch (Exception ex)
            {
                // unload 失败不阻断卸载流程: 即便 ALC 未释放, 文件删除后下次加载会创建新 ALC。
                _logger?.LogWarning(ex, "PluginLoader.UnloadAsync failed for '{Name}'; proceeding with file deletion.", name);
            }
        }

        // 1) 备份到 trash (按时间戳)。
        foreach (var ver in installed.Versions)
        {
            var srcDir = Path.Combine(installed.InstallRoot, ver);
            if (!Directory.Exists(srcDir)) continue;
            var trashPath = Path.Combine(OpenShellPaths.Trash, "providers", name, $"{ver}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
            try
            {
                if (OperatingSystem.IsWindows())
                    Directory.Move(srcDir, trashPath);
                else
                    CopyDirectory(srcDir, trashPath);
                Directory.Delete(srcDir, recursive: true);
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to back up '{Dir}' to trash.", srcDir); }
        }

        // 2) 删除安装根目录与 current 链接。
        if (Directory.Exists(installed.InstallRoot))
        {
            try { Directory.Delete(installed.InstallRoot, recursive: true); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to remove install root '{Dir}'.", installed.InstallRoot); }
        }

        // 3) 从 plugins.config 移除。
        _pluginsConfig.Remove(name);
        await _pluginsConfig.SaveAsync(cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation("Uninstalled provider '{Name}' (backed up to trash).", name);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledProvider> ListInstalled()
    {
        var result = new List<InstalledProvider>();
        if (!Directory.Exists(_providersDir)) return result;

        foreach (var provDir in Directory.EnumerateDirectories(_providersDir))
        {
            var name = Path.GetFileName(provDir);
            var versions = new List<string>();
            string? currentVersion = null;
            foreach (var sub in Directory.EnumerateDirectories(provDir))
            {
                var ver = Path.GetFileName(sub);
                if (string.Equals(ver, "current", StringComparison.OrdinalIgnoreCase))
                {
                    // 解析 current 指向的真实版本。
                    try
                    {
                        var target = ResolveLinkTarget(sub);
                        if (target is not null) currentVersion = Path.GetFileName(target.TrimEnd('/').TrimEnd('\\'));
                    }
                    catch { /* ignore */ }
                    continue;
                }
                versions.Add(ver);
            }
            // 若无 current 子目录但存在 versions, 默认取最新版为 current。
            if (currentVersion is null && versions.Count > 0)
                currentVersion = versions.OrderByDescending(TryParseVersion).First();
            result.Add(new InstalledProvider
            {
                Name = name,
                Versions = versions,
                CurrentVersion = currentVersion,
                InstallRoot = provDir,
            });
        }
        return result;
    }

    /// <inheritdoc />
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        // ADR-0039 §11: 加载 plugins.config.toml, 对每个 Enabled=true 但磁盘上缺失的 provider
        // 重新执行 InstallAsync。单个失败不中断整体恢复流程。
        await _pluginsConfig.LoadAsync(cancellationToken).ConfigureAwait(false);
        var onDisk = ListInstalled().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _pluginsConfig.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Enabled)
            {
                _logger?.LogDebug("Skipping disabled provider '{Name}' during restore.", entry.Name);
                continue;
            }

            // 判断磁盘上是否已就位: 存在对应子目录且有至少一个版本目录。
            if (onDisk.TryGetValue(entry.Name, out var installed)
                && installed.Versions.Count > 0
                && !string.IsNullOrEmpty(installed.CurrentVersion))
            {
                _logger?.LogDebug("Provider '{Name}' already installed (v{Version}); skipping restore.", entry.Name, installed.CurrentVersion);
                continue;
            }

            _logger?.LogInformation("Restoring missing provider '{Name}'...", entry.Name);
            try
            {
                await InstallAsync(entry.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个 provider 恢复失败不阻断其余 provider 的恢复。
                _logger?.LogError(ex, "Failed to restore provider '{Name}'.", entry.Name);
            }
        }
    }

    /// <summary>
    /// 更新 current 符号链接指向指定版本。Per ADR-0039 §6.
    /// Windows 用 junction (无需特权), 其他平台用 symlink; 都失败时回退为目录复制。
    /// </summary>
    private Task<string> UpdateCurrentLinkAsync(string name, string version, CancellationToken ct)
    {
        var provRoot = Path.Combine(_providersDir, SanitiseDir(name));
        var versionDir = Path.Combine(provRoot, version);
        var currentLink = Path.Combine(provRoot, "current");

        // 清理旧 current。
        if (Directory.Exists(currentLink) || File.Exists(currentLink))
        {
            try { Directory.Delete(currentLink, recursive: true); } catch { /* best-effort */ }
            try { File.Delete(currentLink); } catch { /* best-effort */ }
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 创建 junction。用 P/Invoke kernel32.CreateJunction 略复杂, 此处用 cmd mklink /J (需 cmd 进程)。
                // 简化: 改用目录复制 fallback。Windows symlink 普通用户受限。
                CopyDirectory(versionDir, currentLink);
            }
            else
            {
                Directory.CreateSymbolicLink(currentLink, versionDir);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create 'current' link for '{Name}', falling back to directory copy.", name);
            try { CopyDirectory(versionDir, currentLink); } catch { /* best-effort */ }
        }
        return Task.FromResult(currentLink);
    }

    private static bool PathExists(string path)
        => Directory.Exists(path) || File.Exists(path);

    private static void MovePath(string source, string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else if (File.Exists(source)) File.Move(source, destination);
        else throw new FileNotFoundException($"Path to move was not found: {source}", source);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void ValidatePackageVersionSegment(string version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version is "." or ".."
            || !string.Equals(Path.GetFileName(version), version, StringComparison.Ordinal)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new OspPackageException($"Invalid provider version path segment: '{version}'.");
        }
    }

    /// <summary>解析符号链接/junction 指向的目标路径。失败返回 null。</summary>
    private static string? ResolveLinkTarget(string linkPath)
    {
        try
        {
            // .NET 6+ 提供 ResolveLinkTarget。fallback 用 Directory.GetFiles 检查是否存在。
            var fi = new DirectoryInfo(linkPath);
            return fi.LinkTarget;
        }
        catch { return null; }
    }

    /// <summary>递归复制目录 (用于 symlink 不支持时 fallback)。</summary>
    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static string SanitiseDir(string name)
    {
        var invalid = Path.GetInvalidPathChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        return sb.ToString();
    }

    private static Version TryParseVersion(string v)
        => Version.TryParse(v, out var p) ? p : new Version(0, 0);
}
