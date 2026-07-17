using FluentAssertions;
using OpenShell.Recent;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Recent;

/// <summary>
/// ADR-0028 §7: FileRecentService 单元测试。验证 RecordAccess / Clear / Reload /
/// 容量裁剪 / 事件触发 / 损坏行降级。
/// </summary>
public sealed class FileRecentServiceTests
{
    [Fact]
    public void Constructor_MissingFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);
        svc.Recent.Should().BeEmpty();
    }

    [Fact]
    public void RecordAccess_AddsEntryToTop()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        svc.RecordAccess("fs::C:/A");
        svc.RecordAccess("fs::C:/B");

        svc.Recent.Should().HaveCount(2);
        // 最新访问在前。
        svc.Recent[0].Path.Should().Be("fs::C:/B");
        svc.Recent[1].Path.Should().Be("fs::C:/A");
    }

    [Fact]
    public void RecordAccess_ExistingPath_UpdatesTimestampAndMovesToTop()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        svc.RecordAccess("fs::C:/A");
        svc.RecordAccess("fs::C:/B");
        svc.RecordAccess("fs::C:/C");
        // A 当前在最末。再次访问 A, 应移到顶部且时间戳更新。
        svc.RecordAccess("fs::C:/A");

        svc.Recent.Should().HaveCount(3);
        svc.Recent[0].Path.Should().Be("fs::C:/A");
        // B 与 C 顺序保留 (B 在 C 之前被访问过; C 较新)。
        svc.Recent[1].Path.Should().Be("fs::C:/C");
        svc.Recent[2].Path.Should().Be("fs::C:/B");
    }

    [Fact]
    public void RecordAccess_TrimsToMaxEntries()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path, maxEntries: 3);

        svc.RecordAccess("fs::C:/A");
        svc.RecordAccess("fs::C:/B");
        svc.RecordAccess("fs::C:/C");
        svc.RecordAccess("fs::C:/D"); // 超容量, 最旧的 A 被丢弃。

        svc.Recent.Should().HaveCount(3);
        svc.Recent[0].Path.Should().Be("fs::C:/D");
        svc.Recent[1].Path.Should().Be("fs::C:/C");
        svc.Recent[2].Path.Should().Be("fs::C:/B");
    }

    [Fact]
    public void RecordAccess_PersistsToFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        svc.RecordAccess("fs::C:/A");

        File.Exists(path).Should().BeTrue();
        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("\"path\":\"fs::C:/A\"");
        lines[0].Should().Contain("\"ts\":");
    }

    [Fact]
    public void Clear_EmptiesListAndFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);
        svc.RecordAccess("fs::C:/A");
        svc.RecordAccess("fs::C:/B");
        svc.Recent.Should().HaveCount(2);

        svc.Clear();

        svc.Recent.Should().BeEmpty();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Reload_ReReadsFromFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc1 = new FileRecentService(path);
        svc1.RecordAccess("fs::C:/A");

        // 用同一文件构造第二个实例 (模拟外部修改)。
        var svc2 = new FileRecentService(path);
        svc2.Recent.Should().HaveCount(1);
        svc2.Recent[0].Path.Should().Be("fs::C:/A");

        // 通过 svc1 再加一条, 然后让 svc2 reload。
        svc1.RecordAccess("fs::C:/B");
        svc2.Recent.Should().HaveCount(1); // 仍为旧视图
        svc2.Reload();
        svc2.Recent.Should().HaveCount(2);
        svc2.Recent[0].Path.Should().Be("fs::C:/B");
        svc2.Recent[1].Path.Should().Be("fs::C:/A");
    }

    [Fact]
    public void Reload_MissingFile_EmptyList()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        svc.Reload();
        svc.Recent.Should().BeEmpty();
    }

    [Fact]
    public void Reload_InvalidJsonLines_Skipped_NoThrow()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("recent.jsonl",
            "not-json-line\n" +
            "{\"path\":\"fs::C:/good\",\"ts\":\"2026-07-07T15:30:00Z\"}\n" +
            "another-bad-line\n");

        var svc = new FileRecentService(path);

        var act = () => svc.Reload();
        act.Should().NotThrow();
        svc.Recent.Should().ContainSingle();
        svc.Recent[0].Path.Should().Be("fs::C:/good");
    }

    [Fact]
    public void Constructor_InvalidJsonLines_Skipped_NoThrow()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("recent.jsonl",
            "not-json-line\n" +
            "{\"path\":\"fs::C:/good\",\"ts\":\"2026-07-07T15:30:00Z\"}\n");

        var act = () => new FileRecentService(path);
        act.Should().NotThrow();
        var svc = new FileRecentService(path);
        svc.Recent.Should().ContainSingle();
        svc.Recent[0].Path.Should().Be("fs::C:/good");
    }

    [Fact]
    public void RecentChanged_FiresOnRecordAccess()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        var fired = 0;
        svc.RecentChanged += (s, e) => fired++;

        svc.RecordAccess("fs::C:/A");
        fired.Should().Be(1);
    }

    [Fact]
    public void RecentChanged_FiresOnClear()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);
        svc.RecordAccess("fs::C:/A");

        var fired = 0;
        svc.RecentChanged += (s, e) => fired++;

        svc.Clear();
        fired.Should().Be(1);
    }

    [Fact]
    public void RecentChanged_FiresOnReload()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        var fired = 0;
        svc.RecentChanged += (s, e) => fired++;

        svc.Reload();
        fired.Should().Be(1);
    }

    [Fact]
    public void MultipleEntries_RoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc1 = new FileRecentService(path);
        svc1.RecordAccess("fs::C:/A");
        svc1.RecordAccess("fs::C:/B");
        svc1.RecordAccess("s3://my-bucket");

        // 新实例从同一文件加载, 验证 round-trip。
        var svc2 = new FileRecentService(path);
        svc2.Recent.Should().HaveCount(3);
        svc2.Recent[0].Path.Should().Be("s3://my-bucket");
        svc2.Recent[1].Path.Should().Be("fs::C:/B");
        svc2.Recent[2].Path.Should().Be("fs::C:/A");
        // 时间戳必须保留。
        svc2.Recent[0].Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Constructor_CustomMaxEntries_DefaultIs20()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        // 默认 maxEntries = 20 (per ADR-0028 §7)。
        var svc = new FileRecentService(path);
        for (int i = 0; i < 25; i++)
        {
            svc.RecordAccess($"fs::C:/{i}");
        }

        svc.Recent.Should().HaveCount(20);
        // 最新 (24) 应在最前, 最旧保留下来的应是 5 (0-4 被裁掉)。
        svc.Recent[0].Path.Should().Be("fs::C:/24");
        svc.Recent[19].Path.Should().Be("fs::C:/5");
    }

    [Fact]
    public void Constructor_NonPositiveMaxEntries_FallsBackToDefault()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        // 非正数回退到默认值 20。
        var svc = new FileRecentService(path, maxEntries: 0);
        for (int i = 0; i < 25; i++)
        {
            svc.RecordAccess($"fs::C:/{i}");
        }
        svc.Recent.Should().HaveCount(20);
    }

    [Fact]
    public void RecordAccess_EmptyPath_NoEffect()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc = new FileRecentService(path);

        svc.RecordAccess("");
        svc.RecordAccess(null!);
        svc.Recent.Should().BeEmpty();
    }

    [Fact]
    public void RecordAccess_PersistsInMostRecentFirstOrder()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "recent.jsonl");
        var svc1 = new FileRecentService(path);
        svc1.RecordAccess("fs::C:/A");
        svc1.RecordAccess("fs::C:/B");
        svc1.RecordAccess("fs::C:/C");

        // 文件中的顺序应为: C, B, A (最新在前)。
        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("\"path\":\"fs::C:/C\"");
        lines[1].Should().Contain("\"path\":\"fs::C:/B\"");
        lines[2].Should().Contain("\"path\":\"fs::C:/A\"");
    }
}
