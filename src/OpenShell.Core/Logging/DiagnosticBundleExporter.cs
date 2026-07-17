using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenShell.Logging;

/// <summary>
/// 诊断包导出器。Per ADR-0031 §7, §9.
/// 将最近日志、系统信息、环境变量 (脱敏) 打包为 zip, 便于用户报告问题时附带。
/// 输出: <c>{outputDir}/diagnostic-bundle-{timestamp}.zip</c>, 内含:
/// <list type="bullet">
///   <item><c>logs.txt</c> — 最近 500 条日志条目 (来自 <see cref="ILogStore.Recent"/>)。</item>
///   <item><c>system-info.json</c> — OS / .NET 版本 / OpenShell 版本 / 配置 dump。</item>
///   <item><c>env-vars.txt</c> — 环境变量 (含 key/secret/token/password 的脱敏为 ***)。</item>
/// </list>
/// </summary>
public sealed class DiagnosticBundleExporter
{
    private static readonly string[] SensitiveKeywords = { "key", "secret", "token", "password" };

    private readonly ILogStore _logStore;
    private readonly string _outputDir;

    /// <summary>构造 DiagnosticBundleExporter。</summary>
    /// <param name="logStore">日志存储, 用于读取最近日志条目。</param>
    /// <param name="outputDir">zip 输出目录; 不存在时自动创建。</param>
    public DiagnosticBundleExporter(ILogStore logStore, string outputDir)
    {
        _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
        _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
    }

    /// <summary>
    /// 导出诊断包到 <c>{OutputDir}/diagnostic-bundle-{timestamp}.zip</c>。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>生成的 zip 文件绝对路径。</returns>
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_outputDir);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(_outputDir, $"diagnostic-bundle-{timestamp}.zip");

        await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteLogsEntry(archive);
            ct.ThrowIfCancellationRequested();
            WriteSystemInfoEntry(archive);
            ct.ThrowIfCancellationRequested();
            WriteEnvVarsEntry(archive);
        }

        return zipPath;
    }

    private void WriteLogsEntry(ZipArchive archive)
    {
        var entry = archive.CreateEntry("logs.txt");
        using var writer = new StreamWriter(entry.Open());
        var entries = _logStore.Recent(500);
        if (entries.Count == 0)
        {
            writer.WriteLine("(no log entries)");
            return;
        }

        foreach (var e in entries)
        {
            writer.WriteLine(FormatLogEntry(e));
        }
    }

    private static string FormatLogEntry(LogEntry e)
    {
        var ts = e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var level = e.Level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => e.Level.ToString().ToUpperInvariant(),
        };
        var line = $"{ts} [{level}] [{e.Category}] {e.Message}";
        if (e.Exception is { } ex)
        {
            line += " | exception: " + ex.GetType().Name + ": " + ex.Message;
        }
        return line;
    }

    private void WriteSystemInfoEntry(ZipArchive archive)
    {
        var entry = archive.CreateEntry("system-info.json");
        using var writer = new StreamWriter(entry.Open());

        var info = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["os"] = Environment.OSVersion.ToString(),
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["openshellVersion"] = typeof(OpenShellPaths).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["machineName"] = Environment.MachineName,
            ["processId"] = Environment.ProcessId,
            ["configPath"] = OpenShellPaths.Config,
            ["configDump"] = ReadConfigDump(),
            ["providersLoaded"] = "(not available without IProviderRegistry)",
        };

        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        writer.Write(json);
    }

    private static string ReadConfigDump()
    {
        try
        {
            if (File.Exists(OpenShellPaths.Config))
            {
                return File.ReadAllText(OpenShellPaths.Config);
            }
        }
        catch
        {
            // 读取失败时返回占位符, 不阻断诊断包生成。
        }
        return "(config file not found or unreadable)";
    }

    private void WriteEnvVarsEntry(ZipArchive archive)
    {
        var entry = archive.CreateEntry("env-vars.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.WriteLine("# Environment variables (secrets redacted)");
        writer.WriteLine();

        var vars = Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry kv in vars)
        {
            var name = kv.Key?.ToString() ?? "";
            var value = kv.Value?.ToString() ?? "";
            if (IsSensitiveName(name))
            {
                value = "***";
            }
            writer.WriteLine($"{name}={value}");
        }
    }

    private static bool IsSensitiveName(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var keyword in SensitiveKeywords)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
