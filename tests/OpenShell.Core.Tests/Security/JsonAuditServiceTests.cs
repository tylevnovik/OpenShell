using System.IO;
using System.Text.Json;
using FluentAssertions;
using OpenShell.Security;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Security;

/// <summary>
/// ADR-0036 §5: JsonAuditService 单测。
/// 用 TempDir 隔离 JSONL 文件, 验证:
/// - LogAsync + QueryAsync round-trip
/// - ClearAsync 清除
/// - JSONL 格式正确 (每行一个 JSON)
/// - 多条追加 + 按时间过滤
/// </summary>
public class JsonAuditServiceTests
{
    [Fact]
    public async Task LogAsync_CreatesFileWithSingleEntry()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path, user: "alice");

        var entry = MakeEntry("remove-item", "fs::C:/sensitive", OperationRisk.High, approved: true, approvedBy: "prompt");
        await svc.LogAsync(entry);

        File.Exists(path).Should().BeTrue();
        var lines = await File.ReadAllLinesAsync(path);
        lines.Length.Should().Be(1);
        lines[0].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogAsync_ThenQueryAsync_ReturnsSameEntry()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path, user: "alice");

        var entry = MakeEntry("remove-item", "fs::C:/sensitive", OperationRisk.Critical, approved: true, approvedBy: "prompt");
        await svc.LogAsync(entry);

        var results = await svc.QueryAsync();

        results.Should().HaveCount(1);
        var r = results[0];
        r.Command.Should().Be("remove-item");
        r.Args.Should().Be("fs::C:/sensitive");
        r.Risk.Should().Be(OperationRisk.Critical);
        r.Approved.Should().BeTrue();
        r.ApprovedBy.Should().Be("prompt");
        r.User.Should().Be("alice");
        r.Timestamp.Should().Be(entry.Timestamp);
    }

    [Fact]
    public async Task LogAsync_MultipleEntries_AppendsAsJsonLines()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path);

        await svc.LogAsync(MakeEntry("remove-item", "fs::a", OperationRisk.High, true, "prompt"));
        await svc.LogAsync(MakeEntry("copy-item", "fs::b", OperationRisk.Medium, true, "auto"));
        await svc.LogAsync(MakeEntry("set-content", "fs::c", OperationRisk.High, false, "force"));

        var lines = await File.ReadAllLinesAsync(path);
        lines.Length.Should().Be(3);
        // 每行应该是合法 JSON。
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            doc.RootElement.GetProperty("command").GetString().Should().NotBeNullOrEmpty();
        }

        var results = await svc.QueryAsync();
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryAsync_SinceFilter_FiltersOlderEntries()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path);

        var oldTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newTs = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        var cutoff = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await svc.LogAsync(MakeEntry("old-cmd", "fs::old", OperationRisk.High, true, "prompt") with { Timestamp = oldTs });
        await svc.LogAsync(MakeEntry("new-cmd", "fs::new", OperationRisk.High, true, "prompt") with { Timestamp = newTs });

        var results = await svc.QueryAsync(since: cutoff);

        results.Should().HaveCount(1);
        results[0].Command.Should().Be("new-cmd");
    }

    [Fact]
    public async Task QueryAsync_EmptyFile_ReturnsEmptyList()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path);

        var results = await svc.QueryAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAsync_RemovesFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path);
        await svc.LogAsync(MakeEntry("remove-item", "fs::a", OperationRisk.High, true, "prompt"));
        File.Exists(path).Should().BeTrue();

        await svc.ClearAsync();

        File.Exists(path).Should().BeFalse();
        var results = await svc.QueryAsync();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAsync_WhenFileMissing_DoesNotThrow()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path);

        var act = async () => await svc.ClearAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task QueryAsync_SkipsCorruptedLines()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var dirPath = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dirPath);

        // 使用与 JsonAuditService 相同的 camelCase 序列化选项, 确保反序列化能匹配。
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        // 写入: 合法行 + 损坏行 + 合法行
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(MakeEntry("cmd-1", "fs::a", OperationRisk.High, true, "prompt"), opts) + "\n"
            + "this is not valid json {{{\n"
            + JsonSerializer.Serialize(MakeEntry("cmd-2", "fs::b", OperationRisk.High, true, "prompt"), opts) + "\n");

        var svc = new JsonAuditService(filePath: path);

        var results = await svc.QueryAsync();

        // 损坏行跳过, 不抛异常, 仅返回 2 条合法行。
        results.Should().HaveCount(2);
        results[0].Command.Should().Be("cmd-1");
        results[1].Command.Should().Be("cmd-2");
    }

    [Fact]
    public async Task LogAsync_OnUnix_SetsUserOnlyFilePermissions()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Windows 跳过: chmod 600 不适用。
            return;
        }

        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path);

        await svc.LogAsync(MakeEntry("remove-item", "fs::a", OperationRisk.High, true, "prompt"));

        var mode = File.GetUnixFileMode(path);
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void CurrentUser_DefaultsToEnvironmentUserName()
    {
        var svc = new JsonAuditService(filePath: Path.GetTempFileName());
        svc.CurrentUser.Should().Be(Environment.UserName);
    }

    [Fact]
    public async Task LogAsync_EmptyUser_FillsFromCurrentUser()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var svc = new JsonAuditService(filePath: path, user: "bob");

        // 显式传入空 user 字段 → JsonAuditService 应该用 CurrentUser "bob" 填充
        var entry = new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            User: "",
            Command: "remove-item",
            Args: "fs::a",
            Risk: OperationRisk.High,
            Approved: true,
            ApprovedBy: "prompt");
        await svc.LogAsync(entry);

        var results = await svc.QueryAsync();
        results.Should().HaveCount(1);
        results[0].User.Should().Be("bob");
    }

    private static AuditEntry MakeEntry(string command, string args, OperationRisk risk, bool approved, string approvedBy)
        => new(
            Timestamp: DateTimeOffset.UtcNow,
            User: "",
            Command: command,
            Args: args,
            Risk: risk,
            Approved: approved,
            ApprovedBy: approvedBy);
}
