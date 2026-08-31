using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenShell.Updates;

/// <summary>
/// 基于 GitHub Releases 的 <see cref="IUpdateService"/> 实现。Per ADR-0037 §3.
/// 检查更新走 GET /repos/{owner}/{repo}/releases；下载走 asset browser_download_url；
/// 安装走进程内 atomic rename (创建 .old 备份) 或独立 <c>openshell-updater</c> 进程 (ADR-0037 §6)。
/// </summary>
/// <remarks>
/// ADR-0037 §4: HTTP Range 断点续传 (.partial 文件 + Range header)。
/// ADR-0037 §5: Authenticode / Developer ID 代码签名校验 (委托 ICodeSignatureVerifier)。
/// ADR-0037 §6: 优先调用独立 openshell-updater 进程做替换 (避开运行中 exe 锁), fallback 到 in-process。
/// ADR-0037 §8: 增量补丁 (PatchInfo, BinaryPatcher), 失败回退到全量下载。
/// ADR-0037 §12: 企业策略 (IEnterprisePolicyService) 限制 UpdatesEnabled / TargetVersion。
/// ADR-0037 §13: InstallFromOfflineAsync 支持离线包安装。
/// </remarks>
public class GitHubReleasesUpdateService : IUpdateService
{
    private const string GitHubApiBase = "https://api.github.com/repos/";
    private const string UpdaterExeName = "openshell-updater";
    private const string UpdaterExeNameWin = "openshell-updater.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly string? _authToken;
    private readonly string _updatesDir;
    private readonly Subject<UpdateStatus> _status = new();
    private readonly ICodeSignatureVerifier? _signatureVerifier;
    private readonly IEnterprisePolicyService? _enterprisePolicy;
    private readonly ILogger<GitHubReleasesUpdateService>? _logger;

    /// <summary>构造 GitHubReleasesUpdateService。</summary>
    /// <param name="http">注入的 HttpClient (推荐由 IHttpClientFactory 创建)。</param>
    /// <param name="repoOwner">GitHub 仓库 owner (默认 "openshell-org")。</param>
    /// <param name="repoName">GitHub 仓库 name (默认 "openshell")。</param>
    /// <param name="authToken">可选 GitHub Personal Access Token (提高 API 速率限制)。</param>
    /// <param name="updatesDir">下载临时目录 (默认 <see cref="OpenShellPaths.UpdatesDir"/>；测试可注入)。</param>
    /// <param name="signatureVerifier">代码签名校验器 (null 时跳过 Authenticode 校验)。</param>
    /// <param name="enterprisePolicy">企业策略服务 (null 时不应用策略)。</param>
    /// <param name="logger">可选日志器。</param>
    public GitHubReleasesUpdateService(
        HttpClient http,
        string repoOwner = "openshell-org",
        string repoName = "openshell",
        string? authToken = null,
        string? updatesDir = null,
        ICodeSignatureVerifier? signatureVerifier = null,
        IEnterprisePolicyService? enterprisePolicy = null,
        ILogger<GitHubReleasesUpdateService>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _repoOwner = repoOwner ?? throw new ArgumentNullException(nameof(repoOwner));
        _repoName = repoName ?? throw new ArgumentNullException(nameof(repoName));
        _authToken = authToken;
        _updatesDir = updatesDir ?? OpenShellPaths.UpdatesDir;
        _signatureVerifier = signatureVerifier;
        _enterprisePolicy = enterprisePolicy;
        _logger = logger;
    }

    /// <summary>
    /// 是否在 CheckForUpdatesAsync 中包含预发布版本。Per ADR-0037 §2 / §14.
    /// 默认 false。由命令层从 <c>config.IncludePrerelease</c> 注入。
    /// </summary>
    public bool IncludePrerelease { get; set; }

    /// <inheritdoc />
    public IObservable<UpdateStatus> StatusChanged => _status;

    /// <inheritdoc />
    public async ValueTask<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        // ADR-0037 §12: 企业策略禁用更新 → 直接返回 null。
        if (_enterprisePolicy is { UpdatesEnabled: false })
        {
            _logger?.LogInformation("Enterprise policy disables updates; CheckForUpdatesAsync returns null.");
            _status.OnNext(UpdateStatus.Idle);
            return null;
        }

        _status.OnNext(UpdateStatus.Checking);
        try
        {
            var url = $"{GitHubApiBase}{_repoOwner}/{_repoName}/releases";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyGithubHeaders(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonOptions);
            if (releases is null || releases.Count == 0)
            {
                _status.OnNext(UpdateStatus.Idle);
                return null;
            }

            var rid = RuntimeInformation.RuntimeIdentifier;
            var currentVersion = ResolveCurrentVersion();

            // ADR-0037 §12: 企业策略锁定 targetVersion 时, 只匹配该版本。
            string? policyTarget = _enterprisePolicy?.TargetVersion;
            if (policyTarget is not null && !Version.TryParse(policyTarget, out _))
            {
                _logger?.LogWarning("Enterprise policy targetVersion '{V}' is not a valid version; ignoring.", policyTarget);
                policyTarget = null;
            }

            foreach (var rel in releases)
            {
                if (rel.Prerelease && !IncludePrerelease) continue; // 默认跳过预发布，由 IncludePrerelease 控制
                if (!TryParseVersion(rel.TagName, out var ver)) continue;
                if (currentVersion is not null && ver <= currentVersion) continue;

                // 企业策略锁定版本: 仅返回该版本 (不要求高于当前版本)。
                if (policyTarget is not null && ver.ToString() != policyTarget && rel.TagName.TrimStart('v', 'V') != policyTarget)
                    continue;

                var asset = MatchAsset(rel.Assets, rid);
                if (asset is null) continue;

                // 尝试匹配补丁资产 (current -> target)。
                var patchInfo = TryMatchPatchAsset(rel.Assets, rid, currentVersion, ver);

                var info = new UpdateInfo(
                    Version: ver,
                    ReleaseNotes: rel.Body ?? "",
                    DownloadUrl: new Uri(asset.BrowserDownloadUrl),
                    Sha256: asset.Digest ?? "", // GitHub Releases asset 通常不提供 SHA256；DownloadAsync 会跳过校验当 Sha256 为空
                    SizeBytes: asset.Size,
                    PublishedAt: rel.PublishedAt ?? DateTimeOffset.UtcNow,
                    IsPrerelease: rel.Prerelease)
                {
                    Patch = patchInfo,
                };

                _status.OnNext(UpdateStatus.UpdateAvailable);
                return info;
            }

            _status.OnNext(UpdateStatus.Idle);
            return null;
        }
        catch
        {
            _status.OnNext(UpdateStatus.Failed);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        _status.OnNext(UpdateStatus.Downloading);
        try
        {
            Directory.CreateDirectory(_updatesDir);
            var versionDir = Path.Combine(_updatesDir, info.Version.ToString());
            Directory.CreateDirectory(versionDir);

            var filename = GetFilenameFromUrl(info.DownloadUrl);
            var partialPath = Path.Combine(versionDir, filename + ".partial");
            var finalPath = Path.Combine(versionDir, filename);

            // ADR-0037 §8: 若有匹配的补丁, 优先尝试增量更新; 失败回退到全量。
            if (info.Patch is { } patch && await TryApplyPatchAsync(info, patch, versionDir, finalPath, progress, ct).ConfigureAwait(false))
            {
                // 补丁已成功应用并落到 finalPath。跳过全量下载, 直接进入校验阶段。
            }
            else
            {
                await DownloadWithResumeAsync(info, partialPath, finalPath, progress, ct).ConfigureAwait(false);
            }

            // 校验阶段: SHA256 → Authenticode / Developer ID。
            _status.OnNext(UpdateStatus.Verifying);
            if (!string.IsNullOrEmpty(info.Sha256))
            {
                if (!await VerifySha256Async(finalPath, info.Sha256, ct).ConfigureAwait(false))
                {
                    TryDelete(finalPath);
                    _status.OnNext(UpdateStatus.Failed);
                    throw new InvalidOperationException(
                        $"SHA256 verification failed for {info.Version}. Downloaded file deleted.");
                }
            }

            // ADR-0037 §5: 平台代码签名 (Authenticode / Developer ID)。
            if (_signatureVerifier is not null)
            {
                var sigOk = await _signatureVerifier.VerifyAsync(finalPath, ct).ConfigureAwait(false);
                if (!sigOk)
                {
                    TryDelete(finalPath);
                    _status.OnNext(UpdateStatus.Failed);
                    throw new InvalidOperationException(
                        $"Code signature verification failed for '{finalPath}'. The package is unsigned or signed by an untrusted publisher.");
                }
            }

            _status.OnNext(UpdateStatus.ReadyToInstall);
        }
        catch
        {
            _status.OnNext(UpdateStatus.Failed);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask InstallAsync(UpdateInfo info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        _status.OnNext(UpdateStatus.Installing);
        try
        {
            var currentExe = ResolveCurrentExecutablePath();
            if (string.IsNullOrEmpty(currentExe))
            {
                throw new InvalidOperationException("Cannot locate current executable path; cannot perform in-place update.");
            }
            var filename = GetFilenameFromUrl(info.DownloadUrl);
            var versionDir = Path.Combine(_updatesDir, info.Version.ToString());
            var downloadedPath = Path.Combine(versionDir, filename);
            if (!File.Exists(downloadedPath))
            {
                throw new FileNotFoundException(
                    $"Downloaded update not found at '{downloadedPath}'. Run DownloadAsync first.", downloadedPath);
            }

            // ADR-0037 §6: 优先调用独立 openshell-updater 进程 (避开 Windows 文件锁)。
            var updaterExe = TryLocateUpdaterExe();
            if (updaterExe is not null)
            {
                LaunchStandaloneUpdater(updaterExe, currentExe, downloadedPath, restart: true);
                _status.OnNext(UpdateStatus.Installed);
                // ADR-0037 §6: standalone updater 已启动, 主进程立即退出, 把文件替换留给 updater 完成。
                // 不再返回到调用方 — 调用方原本期望主进程在 InstallAsync 后退出。
                Environment.Exit(0);
            }

            // Fallback: 未找到 standalone updater, 警告并退回到进程内 atomic rename。
            _logger?.LogWarning(
                "Standalone openshell-updater not found alongside current exe or in '~/.openshell/'; falling back to in-process rename (may fail on Windows if the exe is in use).");

            InstallInProcess(currentExe, downloadedPath);
            _status.OnNext(UpdateStatus.Installed);
            return ValueTask.CompletedTask;
        }
        catch
        {
            _status.OnNext(UpdateStatus.Failed);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask InstallFromOfflineAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Offline package path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Offline package not found.", path);

        _status.OnNext(UpdateStatus.Verifying);
        try
        {
            var currentExe = ResolveCurrentExecutablePath();
            if (string.IsNullOrEmpty(currentExe))
                throw new InvalidOperationException("Cannot locate current executable path; cannot perform offline update.");

            // ADR-0037 §5: 平台代码签名校验 (离线包必须通过签名校验, 防止被替换的本地包)。
            if (_signatureVerifier is not null)
            {
                var sigOk = await _signatureVerifier.VerifyAsync(path, ct).ConfigureAwait(false);
                if (!sigOk)
                {
                    _status.OnNext(UpdateStatus.Failed);
                    throw new InvalidOperationException(
                        $"Code signature verification failed for offline package '{path}'.");
                }
            }

            // 准备安装文件: 拷贝到 updatesDir/offline/<timestamp>/ 下, 避免直接修改用户提供的路径。
            Directory.CreateDirectory(_updatesDir);
            var offlineDir = Path.Combine(_updatesDir, "offline");
            Directory.CreateDirectory(offlineDir);
            var staging = Path.Combine(offlineDir, Path.GetFileName(path));
            File.Copy(path, staging, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            _status.OnNext(UpdateStatus.Installing);

            // 优先走 standalone updater (与 InstallAsync 一致)。
            var updaterExe = TryLocateUpdaterExe();
            if (updaterExe is not null)
            {
                LaunchStandaloneUpdater(updaterExe, currentExe, staging, restart: true);
                _status.OnNext(UpdateStatus.Installed);
                // ADR-0037 §6: standalone updater 已启动, 主进程立即退出。
                Environment.Exit(0);
            }

            // Fallback: 未找到 standalone updater, 警告并退回到进程内 atomic rename。
            _logger?.LogWarning(
                "Standalone openshell-updater not found alongside current exe or in '~/.openshell/'; falling back to in-process rename (may fail on Windows if the exe is in use).");

            InstallInProcess(currentExe, staging);
            _status.OnNext(UpdateStatus.Installed);
        }
        catch
        {
            _status.OnNext(UpdateStatus.Failed);
            throw;
        }
    }

    /// <summary>
    /// 回滚到上一个版本。查找 <c>{currentExe}.old</c>，若存在则恢复之。
    /// </summary>
    /// <returns>恢复成功返回 true；无 .old 文件返回 false。</returns>
    public bool Rollback()
    {
        var currentExe = ResolveCurrentExecutablePath();
        if (string.IsNullOrEmpty(currentExe)) return false;
        var backupPath = currentExe + ".old";
        if (!File.Exists(backupPath)) return false;

        var tempPath = currentExe + ".rollback-tmp";
        try
        {
            // 当前版本先临时挪走
            File.Move(currentExe, tempPath);
            // 恢复 .old 到目标位置
            File.Move(backupPath, currentExe);
            // 之前的当前版本变为新的 .old (可供再次回滚)
            try { File.Move(tempPath, backupPath); } catch { /* best-effort */ }
            return true;
        }
        catch
        {
            // 恢复失败时尝试把临时文件挪回原位
            if (!File.Exists(currentExe) && File.Exists(tempPath))
            {
                try { File.Move(tempPath, currentExe); } catch { /* best-effort */ }
            }
            return false;
        }
    }

    /// <summary>
    /// 解析当前可执行文件路径。生产实现返回 <see cref="Environment.ProcessPath"/>；
    /// 测试可重写以指向 TempDir 内的 dummy 文件。
    /// </summary>
    protected virtual string ResolveCurrentExecutablePath() => Environment.ProcessPath ?? "";

    /// <summary>
    /// 解析当前版本号。生产实现读 Assembly 的 <c>AssemblyVersionAttribute</c>；
    /// 测试可重写以模拟任意"已安装"版本。
    /// </summary>
    protected virtual Version? ResolveCurrentVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            if (asm is not null)
            {
                var v = asm.GetName().Version;
                if (v is not null) return v;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ===== ADR-0037 §4: HTTP Range 断点续传 =====

    private async Task DownloadWithResumeAsync(
        UpdateInfo info, string partialPath, string finalPath, IProgress<double>? progress, CancellationToken ct)
    {
        long existingLength = 0;
        if (File.Exists(partialPath))
        {
            try { existingLength = new FileInfo(partialPath).Length; } catch { /* best-effort */ }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        ApplyGithubHeaders(req);
        if (existingLength > 0)
            req.Headers.Range = new RangeHeaderValue(from: existingLength, to: null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        FileMode fileMode;
        long offsetForProgress;
        if (resp.StatusCode == HttpStatusCode.PartialContent && existingLength > 0)
        {
            // 206: 服务端支持续传, append 到 .partial。
            fileMode = FileMode.Append;
            offsetForProgress = existingLength;
        }
        else if (resp.StatusCode == HttpStatusCode.OK)
        {
            // 200: 服务端不支持 Range 或 .partial 已是完整文件 → 从头覆盖。
            fileMode = FileMode.Create;
            offsetForProgress = 0;
        }
        else
        {
            resp.EnsureSuccessStatusCode();
            return; // unreachable
        }

        // ContentLength 是本次响应体大小 (不含已存在偏移)。
        var chunkLen = resp.Content.Headers.ContentLength ?? 0;
        var totalForProgress = info.SizeBytes > 0
            ? info.SizeBytes
            : (offsetForProgress + chunkLen);

        await using var fs = new FileStream(partialPath, fileMode, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (totalForProgress > 0 && progress is not null)
            {
                progress.Report((offsetForProgress + read) / (double)totalForProgress);
            }
        }
        await fs.FlushAsync(ct).ConfigureAwait(false);
        await fs.DisposeAsync().ConfigureAwait(false);

        // 校验通过 (或未提供校验值)：原子 rename 去 .partial。
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partialPath, finalPath);
    }

    // ===== ADR-0037 §8: 增量补丁 =====

    private async Task<bool> TryApplyPatchAsync(
        UpdateInfo info, PatchInfo patch, string versionDir, string finalPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            var currentExe = ResolveCurrentExecutablePath();
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                _logger?.LogDebug("Patch skipped: current executable not found.");
                return false;
            }

            // 当前版本必须与 patch.PatchFromVersion 匹配。
            var currentVer = ResolveCurrentVersion();
            if (currentVer is null || currentVer.ToString() != patch.PatchFromVersion)
            {
                _logger?.LogDebug("Patch skipped: current version {Cur} != patchFromVersion {From}.",
                    currentVer, patch.PatchFromVersion);
                return false;
            }

            var patchPath = Path.Combine(versionDir, Path.GetFileName(patch.PatchUrl.LocalPath) + ".patch");
            await DownloadFileAsync(patch.PatchUrl, patchPath, progress, ct).ConfigureAwait(false);

            // 校验补丁 SHA256 (若提供)。
            if (!string.IsNullOrEmpty(patch.PatchHash))
            {
                var actualHash = await BinaryPatcher.ComputeSha256HexAsync(patchPath, ct).ConfigureAwait(false);
                if (!string.Equals(actualHash, patch.PatchHash.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    _logger?.LogWarning("Patch SHA256 mismatch (expected {Exp}, got {Act}); falling back to full download.",
                        patch.PatchHash, actualHash);
                    TryDelete(patchPath);
                    return false;
                }
            }

            // 应用补丁: currentExe → finalPath。
            var tempBase = Path.Combine(versionDir, "base.copy");
            File.Copy(currentExe, tempBase, overwrite: true);
            try
            {
                await BinaryPatcher.ApplyAsync(tempBase, patchPath, finalPath, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(tempBase);
            }

            _logger?.LogInformation("Applied incremental patch {From} -> {To} successfully.",
                patch.PatchFromVersion, info.Version);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Patch apply failed; falling back to full download.");
            TryDelete(finalPath);
            return false;
        }
    }

    private async Task DownloadFileAsync(Uri url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyGithubHeaders(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0 && progress is not null) progress.Report(read / (double)total);
        }
    }

    // ===== ADR-0037 §6: 独立 openshell-updater =====

    private string? TryLocateUpdaterExe()
    {
        try
        {
            // 1) alongside current exe。
            var currentExe = ResolveCurrentExecutablePath();
            if (!string.IsNullOrEmpty(currentExe))
            {
                var dir = Path.GetDirectoryName(currentExe);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? UpdaterExeNameWin : UpdaterExeName);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 2) ~/.openshell/ 下。
            var homeCandidate = Path.Combine(OpenShellPaths.Root,
                OperatingSystem.IsWindows() ? UpdaterExeNameWin : UpdaterExeName);
            if (File.Exists(homeCandidate)) return homeCandidate;
        }
        catch { /* best-effort */ }
        return null;
    }

    private void LaunchStandaloneUpdater(string updaterExe, string currentExe, string newExe, bool restart)
    {
        var pid = Environment.ProcessId;
        var args = $"\"{currentExe}\" \"{newExe}\" {pid}{(restart ? " restart" : "")}";
        _logger?.LogInformation("Launching standalone updater '{Exe}' args='{Args}'.", updaterExe, args);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = updaterExe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = System.Diagnostics.Process.Start(psi);
        p?.Dispose();

        // 给 updater 留时间 polling, 然后主进程退出 (由调用方负责 Environment.Exit)。
        _logger?.LogInformation("Standalone updater launched; main process will exit.");
    }

    private void InstallInProcess(string currentExe, string downloadedPath)
    {
        var backupPath = currentExe + ".old";
        var tempPath = currentExe + ".new";

        // 1. 拷贝下载好的新版本到 currentExe.new
        File.Copy(downloadedPath, tempPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            // Linux/macOS: 确保可执行位。
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // 2. 备份当前 exe 为 .old
        if (File.Exists(backupPath)) TryDelete(backupPath);
        try
        {
            File.Move(currentExe, backupPath);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows())
        {
            // Windows: exe 正在运行被锁定。清理 .new 后抛带清晰提示的异常。
            TryDelete(tempPath);
            throw new IOException(
                $"Cannot replace running executable '{currentExe}' on Windows while it is in use. " +
                "Install the openshell-updater tool to enable out-of-process replacement (ADR-0037 §6).",
                ex);
        }

        // 3. 把 .new rename 到目标位置 (atomic)
        try
        {
            File.Move(tempPath, currentExe);
        }
        catch
        {
            // 安装失败：尝试恢复 .old
            if (File.Exists(backupPath))
            {
                try { File.Move(backupPath, currentExe); } catch { /* best-effort */ }
            }
            TryDelete(tempPath);
            throw;
        }

        // 4. .old 文件保留供 rollback；ADR-0037 §7 要求保留 7 天，清理由外部 cron / 用户手动触发
    }

    // ===== Asset 匹配 =====

    private static GitHubAsset? MatchAsset(List<GitHubAsset> assets, string rid)
    {
        var normalized = NormalizeRid(rid);
        foreach (var a in assets)
        {
            var name = a.Name ?? "";
            if (name.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }
        return null;
    }

    private static PatchInfo? TryMatchPatchAsset(List<GitHubAsset> assets, string rid, Version? currentVersion, Version targetVersion)
    {
        if (currentVersion is null) return null;
        // 补丁资产命名约定: openshell-cli-{rid}-{fromVer}-{toVer}.patch
        // 例如 openshell-cli-win-x64-0.1.0-0.2.0.patch
        var normalized = NormalizeRid(rid);
        var expectedFrom = currentVersion.ToString();
        var expectedTo = targetVersion.ToString();
        foreach (var a in assets)
        {
            var name = a.Name ?? "";
            if (!name.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains(normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains(expectedFrom, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains(expectedTo, StringComparison.OrdinalIgnoreCase)) continue;
            return new PatchInfo
            {
                PatchUrl = new Uri(a.BrowserDownloadUrl),
                PatchFromVersion = expectedFrom,
                PatchHash = a.Digest ?? string.Empty,
                PatchSizeBytes = a.Size,
            };
        }
        return null;
    }

    private static string NormalizeRid(string rid)
    {
        // 把 "win10-x64" / "win7-x64" -> "win-x64"；"linux-musl-x64" -> "linux-x64"；"osx.13-arm64 -> "osx-arm64"
        if (string.IsNullOrEmpty(rid)) return rid;
        var parts = rid.Split('-');
        if (parts.Length >= 2)
        {
            // 取第一个 token 作为 OS，最后一个 token 作为 arch
            var os = parts[0].Split('.')[0]; // osx.13 -> osx
            var arch = parts[^1];
            return $"{os}-{arch}";
        }
        return rid;
    }

    private static bool TryParseVersion(string tagName, out Version version)
    {
        // tag 形如 "v0.2.0" / "0.2.0" / "v0.2.0-beta1"
        var s = tagName.TrimStart('v', 'V');
        // 截掉预发布后缀
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out version!);
    }

    private static string GetFilenameFromUrl(Uri uri)
        => Path.GetFileName(uri.LocalPath);

    private static async Task<bool> VerifySha256Async(string filePath, string expectedSha256, CancellationToken ct)
    {
        // GitHub asset digest 通常形如 "sha256:<hex>"，规范化后再做恒定时间比较。
        var expected = expectedSha256.Trim();
        if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            expected = expected["sha256:".Length..];
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            return false;
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        var actual = Convert.FromHexString(Convert.ToHexString(hash));
        var expectedBytes = Convert.FromHexString(expected);
        return CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private void ApplyGithubHeaders(HttpRequestMessage req)
    {
        // GitHub API 要求 User-Agent + Accept: application/vnd.github+json
        req.Headers.UserAgent.ParseAdd("OpenShell-AutoUpdate/1.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(_authToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        }
    }

    // GitHub Releases API 响应模型（仅取需要的字段）。
    private sealed class GitHubRelease
    {
        public string TagName { get; set; } = "";
        public string? Body { get; set; }
        public bool Prerelease { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string BrowserDownloadUrl { get; set; } = "";
        public string? Digest { get; set; }
    }
}
