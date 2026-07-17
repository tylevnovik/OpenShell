using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Pipeline;
using OpenShell.Providers.FileSystem;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.IntegrationTests;

/// <summary>
/// Pipeline 集成测试。Per ADR-0010, ADR-0012, ADR-0033.
/// 用真实 FileSystemProvider + CommandRegistry + PipelineExecutor + TempDir 隔离,
/// 验证 Get-ChildItem | Where-Object / Select-Object 端到端管道行为。
/// </summary>
public class PipelineIntegrationTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    [Fact]
    public async Task GetChildItem_pipes_to_WhereObject_filters()
    {
        // Arrange: 在 data 子目录下创建文件 (.txt 和 .log 混合)。
        _tempDir.CreateFile("data/a.txt", "");
        _tempDir.CreateFile("data/b.log", "");
        _tempDir.CreateFile("data/c.txt", "");

        var (pipeline, ctx) = BuildPipeline();
        var collected = new List<IItem>();

        // Act: get-childitem data | where-object "name ~= '*.txt'"
        //   ~= 是 glob 匹配, 只保留 name 匹配 *.txt 的项。
        var executed = await pipeline.TryExecuteAsync(
            "get-childitem data | where-object \"name ~= '*.txt'\"",
            () => ctx,
            async (c, items) => { await foreach (var i in items) collected.Add(i); });

        // Assert: 只有 .txt 文件通过过滤器。
        executed.Should().BeTrue();
        collected.Should().HaveCount(2);
        collected.Select(i => i.Name).Should().BeEquivalentTo(new[] { "a.txt", "c.txt" });
    }

    [Fact]
    public async Task GetChildItem_pipes_to_SelectObject_projects()
    {
        // Arrange: 在 data 子目录下创建 3 个文件。
        _tempDir.CreateFile("data/a.txt", "");
        _tempDir.CreateFile("data/b.txt", "");
        _tempDir.CreateFile("data/c.txt", "");

        var (pipeline, ctx) = BuildPipeline();
        var collected = new List<IItem>();

        // Act: get-childitem data | select-object -First 2
        //   -First 2 取前 2 项 (流式截断)。
        var executed = await pipeline.TryExecuteAsync(
            "get-childitem data | select-object -First 2",
            () => ctx,
            async (c, items) => { await foreach (var i in items) collected.Add(i); });

        // Assert: 只输出前 2 项。
        executed.Should().BeTrue();
        collected.Should().HaveCount(2);
    }

    /// <summary>构建 PipelineExecutor + CommandContext, 注册 FileSystemProvider 和所有内置命令。</summary>
    private (PipelineExecutor pipeline, CommandContext ctx) BuildPipeline()
    {
        var hostBuilder = new TestHostBuilder(_tempDir);
        hostBuilder.WithProvider(new FileSystemProvider());
        hostBuilder.RegisterCommandsFromAssembly(typeof(GetChildItemCommand).Assembly);
        var ctx = hostBuilder.CreateCommandContext();
        var pipeline = new PipelineExecutor(hostBuilder.Commands);
        return (pipeline, ctx);
    }

    public void Dispose() => _tempDir.Dispose();
}
