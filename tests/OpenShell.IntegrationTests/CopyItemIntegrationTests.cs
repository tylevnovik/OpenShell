using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers.FileSystem;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.IntegrationTests;

/// <summary>
/// Copy-Item 集成测试。Per ADR-0023 M1, ADR-0033.
/// 用真实 FileSystemProvider + OperationEngine + TempDir 隔离, 验证 fs→fs 复制行为。
/// </summary>
public class CopyItemIntegrationTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly TestHostBuilder _hostBuilder;
    private readonly CommandContext _ctx;

    public CopyItemIntegrationTests()
    {
        _hostBuilder = new TestHostBuilder(_tempDir);
        _hostBuilder.WithProvider(new FileSystemProvider());
        _ctx = _hostBuilder.CreateCommandContext();
    }

    [Fact]
    public async Task CopyItem_fs_to_fs_creates_destination()
    {
        // Arrange: 在临时目录下创建源文件。
        _tempDir.CreateFile("source.txt", "hello world");
        var source = ItemPath.Parse("source.txt");
        var dest = ItemPath.Parse("destination.txt");

        // Act: 直接调用 CopyItemCommand (走 OperationEngine.CopyAsync → FileSystemProvider)。
        var cmd = new CopyItemCommand();
        var args = new CopyItemCommand.Args(source, dest);
        await DrainAsync(cmd.ExecuteAsync(args, _ctx, default));

        // Assert: 目标文件存在且内容一致。
        _tempDir.FileExists("destination.txt").Should().BeTrue();
        (await System.IO.File.ReadAllTextAsync(_tempDir.GetFullPath("destination.txt")))
            .Should().Be("hello world");
        // 源文件仍存在 (copy 非移动)。
        _tempDir.FileExists("source.txt").Should().BeTrue();
    }

    [Fact]
    public async Task CopyItem_recursive_copies_directory()
    {
        // Arrange: 创建嵌套目录结构。
        _tempDir.CreateFile("src/a.txt", "aaa");
        _tempDir.CreateFile("src/sub/b.txt", "bbb");
        _tempDir.CreateFile("src/sub/deep/c.txt", "ccc");
        var source = ItemPath.Parse("src");
        var dest = ItemPath.Parse("dst");

        // Act: 递归复制目录。
        var cmd = new CopyItemCommand();
        var args = new CopyItemCommand.Args(source, dest, Recurse: true);
        await DrainAsync(cmd.ExecuteAsync(args, _ctx, default));

        // Assert: 所有嵌套文件都被复制到目标目录。
        _tempDir.FileExists("dst/a.txt").Should().BeTrue();
        _tempDir.FileExists("dst/sub/b.txt").Should().BeTrue();
        _tempDir.FileExists("dst/sub/deep/c.txt").Should().BeTrue();
        (await System.IO.File.ReadAllTextAsync(_tempDir.GetFullPath("dst/a.txt")))
            .Should().Be("aaa");
        (await System.IO.File.ReadAllTextAsync(_tempDir.GetFullPath("dst/sub/deep/c.txt")))
            .Should().Be("ccc");
        // 源目录仍存在 (copy 非移动)。
        _tempDir.DirectoryExists("src").Should().BeTrue();
    }

    /// <summary>消费 IAsyncEnumerable 以触发命令执行。</summary>
    private static async Task DrainAsync(IAsyncEnumerable<IItem> items)
    {
        await foreach (var _ in items) { }
    }

    public void Dispose() => _tempDir.Dispose();
}
