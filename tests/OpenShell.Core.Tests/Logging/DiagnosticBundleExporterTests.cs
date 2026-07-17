using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenShell.Logging;
using Xunit;

namespace OpenShell.Core.Tests.Logging;

/// <summary>
/// DiagnosticBundleExporter 单元测试。Per ADR-0031 §7, §9.
/// 验证 ExportAsync 生成 zip / 包含 logs.txt / system-info.json / env-vars.txt,
/// 且环境变量中含 key/secret/token/password 关键字时被脱敏为 ***。
/// </summary>
public class DiagnosticBundleExporterTests
{
    private static LogEntry MakeEntry(
        string message = "msg",
        LogLevel level = LogLevel.Information,
        string category = "Test",
        DateTimeOffset? timestamp = null,
        Exception? exception = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            Exception = exception,
        };

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openshell-diag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ExportAsync_CreatesZipFile()
    {
        var store = new InMemoryLogStore();
        store.Append(MakeEntry("hello"));
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            File.Exists(zipPath).Should().BeTrue();
            Path.GetExtension(zipPath).Should().Be(".zip");
            Path.GetFileName(zipPath).Should().StartWith("diagnostic-bundle-");
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_ZipContainsAllExpectedEntries()
    {
        var store = new InMemoryLogStore();
        store.Append(MakeEntry("hello world"));
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var entryNames = ReadZipEntryNames(zipPath);
            entryNames.Should().Contain("logs.txt");
            entryNames.Should().Contain("system-info.json");
            entryNames.Should().Contain("env-vars.txt");
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_LogsTxt_ContainsFormattedEntries()
    {
        var store = new InMemoryLogStore();
        store.Append(MakeEntry("first message"));
        store.Append(MakeEntry("second message", LogLevel.Warning, category: "CliHost"));
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var logsContent = ReadZipEntry(zipPath, "logs.txt");
            logsContent.Should().Contain("first message");
            logsContent.Should().Contain("second message");
            logsContent.Should().Contain("[INFO]");
            logsContent.Should().Contain("[WARN]");
            logsContent.Should().Contain("CliHost");
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_LogsTxt_IncludesExceptionInfo()
    {
        var store = new InMemoryLogStore();
        store.Append(MakeEntry("boom", LogLevel.Error, exception: new InvalidOperationException("simulated failure")));
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var logsContent = ReadZipEntry(zipPath, "logs.txt");
            logsContent.Should().Contain("boom");
            logsContent.Should().Contain("exception: InvalidOperationException");
            logsContent.Should().Contain("simulated failure");
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_EmptyLogStore_WritesPlaceholder()
    {
        var store = new InMemoryLogStore();
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var logsContent = ReadZipEntry(zipPath, "logs.txt");
            logsContent.Should().Contain("(no log entries)");
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_SystemInfoJson_IsValidJsonWithExpectedFields()
    {
        var store = new InMemoryLogStore();
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var jsonContent = ReadZipEntry(zipPath, "system-info.json");
            var doc = JsonDocument.Parse(jsonContent);
            doc.RootElement.GetProperty("os").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("runtime").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("openshellVersion").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("machineName").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("configPath").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("processId").GetInt32().Should().BeGreaterThan(0);
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_EnvVars_RedactsSecretsContainingKey()
    {
        // 设置一个带 "KEY" 的环境变量, 验证导出时被脱敏为 ***。
        const string secretName = "OPENSHELL_TEST_API_KEY";
        const string secretValue = "super-secret-value-should-not-leak";
        Environment.SetEnvironmentVariable(secretName, secretValue);
        var store = new InMemoryLogStore();
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var envContent = ReadZipEntry(zipPath, "env-vars.txt");
            envContent.Should().Contain(secretName + "=***");
            envContent.Should().NotContain(secretValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            TryCleanup(outputDir);
        }
    }

    [Theory]
    [InlineData("MY_SECRET", "leak-me")]
    [InlineData("AUTH_TOKEN", "leak-me")]
    [InlineData("DB_PASSWORD", "leak-me")]
    public async Task ExportAsync_EnvVars_RedactsAllSensitiveKeywords(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        var store = new InMemoryLogStore();
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var envContent = ReadZipEntry(zipPath, "env-vars.txt");
            envContent.Should().Contain(name + "=***");
            envContent.Should().NotContain(value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_EnvVars_DoesNotRedactNonSensitive()
    {
        const string name = "OPENSHELL_TEST_PLAIN_VAR";
        const string value = "not-a-secret-plain-value";
        Environment.SetEnvironmentVariable(name, value);
        var store = new InMemoryLogStore();
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();

            var envContent = ReadZipEntry(zipPath, "env-vars.txt");
            envContent.Should().Contain(name + "=" + value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public async Task ExportAsync_CancellationTokenRespected()
    {
        var store = new InMemoryLogStore();
        store.Append(MakeEntry("a"));
        var outputDir = CreateTempDir();

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = async () => await exporter.ExportAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            TryCleanup(outputDir);
        }
    }

    [Fact]
    public void Constructor_NullLogStore_Throws()
    {
        var act = () => new DiagnosticBundleExporter(null!, "/tmp");
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logStore");
    }

    [Fact]
    public void Constructor_NullOutputDir_Throws()
    {
        var store = new InMemoryLogStore();
        var act = () => new DiagnosticBundleExporter(store, null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("outputDir");
    }

    [Fact]
    public async Task ExportAsync_CreatesOutputDir_WhenMissing()
    {
        var store = new InMemoryLogStore();
        var outputDir = Path.Combine(Path.GetTempPath(), "openshell-diag-test-nested-" + Guid.NewGuid().ToString("N"), "sub");
        // outputDir 不存在; ExportAsync 应自动创建。

        try
        {
            var exporter = new DiagnosticBundleExporter(store, outputDir);
            var zipPath = await exporter.ExportAsync();
            File.Exists(zipPath).Should().BeTrue();
            Directory.Exists(outputDir).Should().BeTrue();
        }
        finally
        {
            TryCleanup(Path.GetDirectoryName(outputDir)!);
        }
    }

    private static IReadOnlyList<string> ReadZipEntryNames(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    private static string ReadZipEntry(string zipPath, string entryName)
    {
        using var fs = File.OpenRead(zipPath);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"entry {entryName} not found");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort; tests may run in parallel
        }
    }
}
