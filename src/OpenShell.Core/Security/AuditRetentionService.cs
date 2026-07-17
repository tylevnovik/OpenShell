using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenShell;

namespace OpenShell.Security;

/// <summary>
/// 审计日志保留期清理服务。Per ADR-0036 §5.
/// 作为 <see cref="IHostedService"/> 运行, 每日清理 <c>~/.openshell/audit.jsonl</c> 中超过保留期 (默认 30 天) 的条目。
/// 启动时立即执行一次清理, 之后每 24 小时一次。Unix 下保持 0600 文件权限。
/// </summary>
/// <remarks>
/// 清理采用原子重写: 读取全部行 → 过滤保留 <see cref="_retentionDays"/> 天内条目 → 写入临时文件 →
/// <c>File.Replace</c> / <c>File.Move</c> 替换原文件。读取使用 <see cref="FileShare.ReadWrite"/>
/// 避免与 <see cref="JsonAuditService"/> 的并发追加冲突。
/// </remarks>
public sealed class AuditRetentionService : IHostedService, IDisposable
{
    /// <summary>默认保留期: 30 天。Per ADR-0036 §5.</summary>
    public const int DefaultRetentionDays = 30;

    private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    private readonly string _filePath;
    private readonly int _retentionDays;
    private readonly ILogger<AuditRetentionService>? _logger;
    private Timer? _timer;

    public AuditRetentionService(
        ILogger<AuditRetentionService>? logger = null,
        int retentionDays = DefaultRetentionDays,
        string? filePath = null)
    {
        _filePath = filePath ?? OpenShellPaths.AuditLog;
        _retentionDays = retentionDays > 0 ? retentionDays : DefaultRetentionDays;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动时立即清理一次 (fire-and-forget; PruneAsync 内部捕获异常)。
        _ = PruneAsync(cancellationToken);

        // 之后每 24 小时清理一次。
        _timer = new Timer(
            _ => _ = PruneAsync(CancellationToken.None),
            state: null,
            dueTime: DailyInterval,
            period: DailyInterval);

        _logger?.LogInformation(
            "Audit retention service started (retention={Days}d, path={Path}).", _retentionDays, _filePath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 执行一次清理: 读取审计日志, 保留 <see cref="_retentionDays"/> 天内的条目, 原子重写文件。
    /// 内部捕获所有非取消异常并记录日志 (后台清理不应终止服务)。
    /// </summary>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PruneCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消属正常, 不记录。
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Audit retention pruning failed for '{Path}'.", _filePath);
        }
    }

    private async Task PruneCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return;

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_retentionDays);
        var kept = await ReadAndFilterAsync(cutoff, cancellationToken).ConfigureAwait(false);
        await RewriteAsync(kept, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation(
            "Audit retention pruning complete: kept {Count} entries (cutoff={Cutoff:O}).",
            kept.Count, cutoff);
    }

    private async Task<List<string>> ReadAndFilterAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var kept = new List<string>();
        string text;
        try
        {
            // FileShare.ReadWrite 允许并发写入 (JsonAuditService append)。
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return kept;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.TrimEnd('\r');
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(trimmed, options);
                if (entry is null)
                {
                    kept.Add(trimmed);
                    continue;
                }
                if (entry.Timestamp >= cutoff)
                {
                    kept.Add(trimmed);
                }
            }
            catch (JsonException)
            {
                // 损坏行保留 (不删除未知格式条目, 避免数据丢失)。
                kept.Add(trimmed);
            }
        }

        return kept;
    }

    private async Task RewriteAsync(List<string> kept, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = _filePath + ".tmp";
        await using (var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true))
        await using (var writer = new StreamWriter(stream) { NewLine = "\n" })
        {
            foreach (var line in kept)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        TrySetUserOnlyPermissions(tempPath);

        // 原子替换: Windows 用 File.Replace (保留目标权限); 其他平台用 Move 覆盖。
        if (OperatingSystem.IsWindows() && File.Exists(_filePath))
        {
            File.Replace(tempPath, _filePath, destinationBackupFileName: null);
        }
        else
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(tempPath, _filePath);
        }

        TrySetUserOnlyPermissions(_filePath);
    }

    private static void TrySetUserOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort: 权限失败不阻塞功能。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
    }
}
