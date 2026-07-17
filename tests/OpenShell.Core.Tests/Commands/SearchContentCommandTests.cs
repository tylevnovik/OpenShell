using FluentAssertions;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Core.Tests.TestSupport;
using OpenShell.Items;
using OpenShell.Preview;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// ADR-0030 §5: SearchContentCommand 单测。
/// 验证: 找到含匹配模式的文件, Include glob 过滤, 二进制跳过。
/// </summary>
public class SearchContentCommandTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly TestHostBuilder _hostBuilder;
    private readonly CommandContext _ctx;

    public SearchContentCommandTests()
    {
        _hostBuilder = new TestHostBuilder(_tempDir);
        _hostBuilder.WithProvider(new StubFileProvider());
        _ctx = _hostBuilder.CreateCommandContext();
    }

    [Fact]
    public async Task Execute_FindsFilesContainingPattern()
    {
        // Arrange: 两个文件含 "TODO", 一个不含。
        _tempDir.CreateFile("a.cs", "using System;\n// TODO: fix this\npublic class A { }");
        _tempDir.CreateFile("b.cs", "public class B { }\n// TODO: another task");
        _tempDir.CreateFile("c.cs", "no matches here");

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        // Act
        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        // Assert: 找到 a.cs 和 b.cs, 不含 c.cs。
        results.Should().HaveCount(2);
        results.Select(r => r.Name).Should().Contain(new[] { "a.cs", "b.cs" });

        // 验证 matchedLines 上下文。
        var a = results.OfType<SearchResultItem>().First(r => r.Name == "a.cs");
        a.MatchedLines.Should().NotBeEmpty();
        a.MatchedLines[0].Line.Should().Be(2);
        a.MatchedLines[0].Text.Should().Contain("TODO");
    }

    [Fact]
    public async Task Execute_IncludeGlob_FiltersFiles()
    {
        // Arrange: .cs 含 TODO, .log 也含 TODO, 但只搜 *.cs。
        _tempDir.CreateFile("a.cs", "// TODO: in cs");
        _tempDir.CreateFile("b.log", "TODO: in log");

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: "*.cs");

        // Act
        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        // Assert: 只搜 .cs 文件, 不搜 .log。
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("a.cs");
    }

    [Fact]
    public async Task Execute_BinaryFile_Skipped()
    {
        // Arrange: 含 \0 的二进制文件 (扩展名伪装成 .txt), 含匹配模式字符串但应被跳过。
        var bytes = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x54, 0x4F, 0x44, 0x4F }; // "hello\0TODO"
        var full = System.IO.Path.Combine(_tempDir.FullPath, "bin.txt");
        await System.IO.File.WriteAllBytesAsync(full, bytes);

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        // Act
        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        // Assert: 二进制文件被跳过, 无结果。
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_RecursiveSearch_FindsNestedFiles()
    {
        // Arrange: 嵌套目录中的文件含匹配模式。
        _tempDir.CreateFile("sub/deep/note.md", "# TODO: nested match");

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        // Act
        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        // Assert: 递归找到嵌套文件。
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("note.md");
    }

    [Fact]
    public async Task Execute_NoMatches_ReturnsEmpty()
    {
        _tempDir.CreateFile("a.cs", "nothing here");

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    // ---- 进度报告测试 (Per ADR-0030 §5: 进度更新) ----

    [Fact]
    public async Task Execute_ReportsProgress_StartAndCompletion()
    {
        // Arrange: 创建若干文件确保有扫描进度。
        _tempDir.CreateFile("a.cs", "// TODO: one");
        _tempDir.CreateFile("b.cs", "// TODO: two");
        _tempDir.CreateFile("c.cs", "no match");

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        // Act
        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        // Assert: 进度报告至少包含 start (Completed=0) 和 done (IsCompleted=true)。
        var captured = _hostBuilder.CapturedProgress;
        captured.Should().NotBeNull("TestHost 应捕获进度报告");
        IReadOnlyList<OperationProgress> progress = captured!;
        progress.Should().NotBeEmpty();

        // 首个报告: 开始扫描, Completed=0, IsCompleted=false。
        progress[0].Should().BeEquivalentTo(new { Completed = 0L, IsCompleted = false });

        // 最终报告: 完成, IsCompleted=true, Status="done"。
        progress.Last().IsCompleted.Should().BeTrue();
        progress.Last().Status.Should().Be("done");

        // 最终报告的 Completed 应等于实际扫描的文件数 (3)。
        progress.Last().Completed.Should().Be(3);
        progress.Last().Total.Should().Be(3);
    }

    [Fact]
    public async Task Execute_ReportsProgress_DuringScan_WithFileName()
    {
        // Arrange: 创建多个文件, 验证扫描期间报告含文件名。
        _tempDir.CreateFile("alpha.cs", "// TODO: alpha");
        _tempDir.CreateFile("beta.cs", "// TODO: beta");
        _tempDir.CreateFile("gamma.cs", "// TODO: gamma");

        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        // Act
        await foreach (var _ in cmd.ExecuteAsync(args, _ctx, default)) { }

        // Assert: 中间进度报告的 Status 应含 "scanning:" 前缀 (文件级报告)。
        var progress = _hostBuilder.CapturedProgress!;
        var scanningReports = progress.Where(p => p.Status?.StartsWith("scanning:") == true).ToList();
        scanningReports.Should().NotBeEmpty("应至少有一个文件级扫描进度报告");

        // 至少有一个报告的 Completed >= 1。
        progress.Any(p => p.Completed >= 1).Should().BeTrue();
    }

    [Fact]
    public async Task Execute_NoFiles_StillReportsStartAndDone()
    {
        // Arrange: 空目录, 无文件。
        var cmd = new SearchContentCommand();
        var args = new SearchContentCommand.Args(Path: null, Pattern: "TODO", Include: null);

        // Act
        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, _ctx, default))
            results.Add(item);

        // Assert: 即使无文件, 仍报告 start + done。
        var progress = _hostBuilder.CapturedProgress!;
        progress.Should().HaveCountGreaterOrEqualTo(2, "应至少有 start + done 两个报告");
        progress[0].Completed.Should().Be(0);
        progress[0].IsCompleted.Should().BeFalse();
        progress.Last().IsCompleted.Should().BeTrue();
        progress.Last().Status.Should().Be("done");
        progress.Last().Completed.Should().Be(0);
    }

    public void Dispose() => _tempDir.Dispose();
}
