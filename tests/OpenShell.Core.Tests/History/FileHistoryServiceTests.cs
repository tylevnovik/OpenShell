using FluentAssertions;
using OpenShell.History;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.History;

/// <summary>
/// FileHistoryService 单元测试。Per ADR-0020, ADR-0022 §6, ADR-0033.
/// 用 TempDir 隔离持久化文件; 验证 Add / Clear / Search / Recent / 持久化回写。
/// </summary>
public sealed class FileHistoryServiceTests : IAsyncLifetime
{
    private TempDir _tempDir = null!;
    private string _historyPath = null!;
    private FileHistoryService _service = null!;

    public Task InitializeAsync()
    {
        _tempDir = new TempDir();
        _historyPath = Path.Combine(_tempDir.FullPath, "history.jsonl");
        _service = new FileHistoryService(_historyPath, maxEntries: 5);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _tempDir.Dispose();
    }

    [Fact]
    public void Add_AppendsToRecent()
    {
        _service.Add("get-childitem", success: true, exitCode: 0);
        _service.Add("set-location /", success: true, exitCode: 0);

        _service.Recent.Should().HaveCount(2);
        _service.Recent[0].Command.Should().Be("get-childitem");
        _service.Recent[1].Command.Should().Be("set-location /");
    }

    [Fact]
    public void Add_RecordsSuccessAndExitCode()
    {
        _service.Add("ok-command", success: true, exitCode: 0);
        _service.Add("fail-command", success: false, exitCode: 42);

        _service.Recent.Should().HaveCount(2);
        _service.Recent[0].Success.Should().BeTrue();
        _service.Recent[0].ExitCode.Should().Be(0);
        _service.Recent[1].Success.Should().BeFalse();
        _service.Recent[1].ExitCode.Should().Be(42);
    }

    [Fact]
    public void Add_AssignsNewIdAndTimestamp()
    {
        _service.Add("cmd", success: true, exitCode: 0);
        var entry = _service.Recent[0];
        entry.Id.Should().NotBeEmpty();
        entry.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Add_OverCapacity_TrimsOldestFifo()
    {
        for (int i = 0; i < 7; i++)
        {
            _service.Add($"cmd-{i}", success: true, exitCode: 0);
        }

        _service.Recent.Should().HaveCount(5);
        _service.Recent[0].Command.Should().Be("cmd-2");
        _service.Recent[4].Command.Should().Be("cmd-6");
    }

    [Fact]
    public void Clear_EmptiesInMemory()
    {
        _service.Add("a", success: true, exitCode: 0);
        _service.Add("b", success: true, exitCode: 0);
        _service.Recent.Should().HaveCount(2);

        _service.Clear();

        _service.Recent.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_DeletesPersistenceFile()
    {
        _service.Add("a", success: true, exitCode: 0);
        await _service.DisposeAsync();
        // Re-create service and verify file exists.
        _service = new FileHistoryService(_historyPath, maxEntries: 5);
        File.Exists(_historyPath).Should().BeTrue();

        _service.Clear();

        File.Exists(_historyPath).Should().BeFalse();
    }

    [Fact]
    public void Search_ReturnsMatchingCommands()
    {
        _service.Add("get-childitem", success: true, exitCode: 0);
        _service.Add("set-location /tmp", success: true, exitCode: 0);
        _service.Add("get-item file.txt", success: true, exitCode: 0);

        var results = _service.Search("get-");

        results.Should().HaveCount(2);
        results.Should().Contain(e => e.Command == "get-childitem");
        results.Should().Contain(e => e.Command == "get-item file.txt");
    }

    [Fact]
    public void Search_CaseInsensitive()
    {
        _service.Add("GET-CHILDITEM", success: true, exitCode: 0);

        var results = _service.Search("get-childitem");
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Search_ReturnsMostRecentFirst()
    {
        _service.Add("cmd-a", success: true, exitCode: 0);
        _service.Add("cmd-a", success: true, exitCode: 0);
        _service.Add("cmd-b", success: true, exitCode: 0);
        _service.Add("cmd-a", success: true, exitCode: 0);

        var results = _service.Search("cmd-a");
        results.Should().HaveCount(3);
        // 倒序: 最近 (最后添加的) 在前。
        results[0].Id.Should().NotBe(results[1].Id);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        _service.Add("cmd", success: true, exitCode: 0);

        _service.Search("").Should().BeEmpty();
        _service.Search(null!).Should().BeEmpty();
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        _service.Add("cmd", success: true, exitCode: 0);

        _service.Search("xyz").Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_FlushesToPersistenceFile()
    {
        _service.Add("persisted-cmd", success: true, exitCode: 0);

        await _service.DisposeAsync();

        File.Exists(_historyPath).Should().BeTrue();
        var lines = await File.ReadAllLinesAsync(_historyPath);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("persisted-cmd");
    }

    [Fact]
    public async Task Constructor_LoadsExistingFile()
    {
        // 先写一个文件再创建 service, 验证加载已有记录。
        await File.WriteAllTextAsync(_historyPath,
            "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"command\":\"loaded-cmd\",\"success\":true,\"exitCode\":0,\"workingDirectory\":{\"provider\":\"fs\",\"internalPath\":\"/\"}}\n");

        var service = new FileHistoryService(_historyPath, maxEntries: 5);
        try
        {
            service.Recent.Should().HaveCount(1);
            service.Recent[0].Command.Should().Be("loaded-cmd");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task Constructor_SkipsCorruptedLines()
    {
        await File.WriteAllTextAsync(_historyPath,
            "not-json-line\n" +
            "{\"id\":\"22222222-2222-2222-2222-222222222222\",\"timestamp\":\"2024-01-01T00:00:00Z\",\"command\":\"good-cmd\",\"success\":true,\"exitCode\":0,\"workingDirectory\":{\"provider\":\"fs\",\"internalPath\":\"/\"}}\n");

        var service = new FileHistoryService(_historyPath, maxEntries: 5);
        try
        {
            // 损坏的行被跳过, 仅加载有效记录。
            service.Recent.Should().HaveCount(1);
            service.Recent[0].Command.Should().Be("good-cmd");
        }
        finally
        {
            await service.DisposeAsync();
        }
    }
}
