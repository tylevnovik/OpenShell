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
/// New-Item / Remove-Item 集成测试。Per ADR-0023 M1, ADR-0033.
/// 用真实 FileSystemProvider + OperationEngine + TempDir 隔离, 验证文件/目录创建与删除。
/// </summary>
public class NewRemoveItemIntegrationTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly TestHostBuilder _hostBuilder;
    private readonly CommandContext _ctx;

    public NewRemoveItemIntegrationTests()
    {
        _hostBuilder = new TestHostBuilder(_tempDir);
        _hostBuilder.WithProvider(new FileSystemProvider());
        _ctx = _hostBuilder.CreateCommandContext();
    }

    [Fact]
    public async Task NewItem_file_creates_file()
    {
        // Arrange
        var path = ItemPath.Parse("newfile.txt");

        // Act: New-Item -Path newfile.txt -Type file (默认 type=file)
        var cmd = new NewItemCommand();
        var args = new NewItemCommand.Args(path, Type: "file");
        await DrainAsync(cmd.ExecuteAsync(args, _ctx, default));

        // Assert: 文件被创建。
        _tempDir.FileExists("newfile.txt").Should().BeTrue();
    }

    [Fact]
    public async Task NewItem_directory_creates_directory()
    {
        // Arrange
        var path = ItemPath.Parse("newdir");

        // Act: New-Item -Path newdir -Type directory
        var cmd = new NewItemCommand();
        var args = new NewItemCommand.Args(path, Type: "directory");
        await DrainAsync(cmd.ExecuteAsync(args, _ctx, default));

        // Assert: 目录被创建。
        _tempDir.DirectoryExists("newdir").Should().BeTrue();
    }

    [Fact]
    public async Task RemoveItem_file_deletes()
    {
        // Arrange: 创建待删除文件。
        _tempDir.CreateFile("todelete.txt", "content");
        _tempDir.FileExists("todelete.txt").Should().BeTrue();

        var path = ItemPath.Parse("todelete.txt");

        // Act: Remove-Item -Path todelete.txt -Force (Force=true → 物理删除, 不走 trash)
        var cmd = new RemoveItemCommand();
        var args = new RemoveItemCommand.Args(path, Force: true);
        await DrainAsync(cmd.ExecuteAsync(args, _ctx, default));

        // Assert: 文件已删除。
        _tempDir.FileExists("todelete.txt").Should().BeFalse();
    }

    [Fact]
    public async Task RemoveItem_recursive_deletes_directory()
    {
        // Arrange: 创建嵌套目录结构。
        _tempDir.CreateFile("delparent/a.txt", "");
        _tempDir.CreateFile("delparent/sub/b.txt", "");
        _tempDir.DirectoryExists("delparent").Should().BeTrue();

        var path = ItemPath.Parse("delparent");

        // Act: Remove-Item -Path delparent -Recurse -Force
        var cmd = new RemoveItemCommand();
        var args = new RemoveItemCommand.Args(path, Recurse: true, Force: true);
        await DrainAsync(cmd.ExecuteAsync(args, _ctx, default));

        // Assert: 整个目录树被递归删除。
        _tempDir.DirectoryExists("delparent").Should().BeFalse();
        _tempDir.FileExists("delparent/a.txt").Should().BeFalse();
        _tempDir.FileExists("delparent/sub/b.txt").Should().BeFalse();
    }

    /// <summary>消费 IAsyncEnumerable 以触发命令执行。</summary>
    private static async Task DrainAsync(IAsyncEnumerable<IItem> items)
    {
        await foreach (var _ in items) { }
    }

    public void Dispose() => _tempDir.Dispose();
}
