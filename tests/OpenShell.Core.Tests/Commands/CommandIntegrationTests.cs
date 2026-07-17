#nullable enable
// 命令集成测试：用真实 FileSystemProvider + 真实文件系统操作验证常用命令。
// 与 StubItemProvider 单元测试不同，这里走完整的命令执行链路：
//   TestHostBuilder → 真实 ProviderRegistry → 真实 CommandRegistry → 命令 ExecuteAsync → 真实文件系统
// 覆盖 cd/pwd/ls/mkdir/rm/cp/mv/cat/echo 等高频命令的真实行为。
// Per ADR-0033 §3: 集成测试用真实实现（非 mock），临时目录隔离。

using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Providers.FileSystem;
using OpenShell.TestUtils;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// 命令集成测试：真实文件系统 + 真实 Provider + 真实命令执行。
/// 与 StubItemProvider 单元测试互补——这里验证端到端行为正确性。
/// </summary>
public class CommandIntegrationTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly TestHostBuilder _builder;
    private readonly IServiceProvider _provider;
    private readonly IHost _host;
    private readonly ICommandRegistry _commands;
    private readonly IProviderRegistry _providers;
    private readonly IErrorStream _errors;
    private readonly IVariableRegistry _vars;

    public CommandIntegrationTests()
    {
        _builder = new TestHostBuilder(_tempDir);
        // 注册真实 FileSystemProvider
        _builder.WithProvider(new FileSystemProvider());
        // 注册所有 builtin 命令（从 OpenShell.Core 程序集扫描）
        _builder.RegisterCommandsFromAssembly(typeof(GetChildItemCommand).Assembly);
        _builder.PopulateBuiltins();

        _provider = _builder.Build();
        _host = _provider.GetRequiredService<IHost>();
        _commands = _provider.GetRequiredService<ICommandRegistry>();
        _providers = _provider.GetRequiredService<IProviderRegistry>();
        _errors = _provider.GetRequiredService<IErrorStream>();
        _vars = _provider.GetRequiredService<IVariableRegistry>();
    }

    public void Dispose() => _tempDir.Dispose();

    // ---------------------------------------------------------------------
    // 辅助方法：执行命令 + 断言
    // ---------------------------------------------------------------------

    private CommandContext CreateContext() => _builder.CreateCommandContext();

    /// <summary>在指定位置创建 CommandContext（用于连续命令测试）。</summary>
    private CommandContext CreateContextAt(ItemPath location)
    {
        var ctx = _builder.CreateCommandContext();
        // CommandContext.CurrentLocation 是 init 只读快照，Host.CurrentLocation 才是可变状态。
        // 通过反射设置 ctx.CurrentLocation（测试专用 hack）。
        var t = typeof(CommandContext);
        var prop = t.GetProperty("CurrentLocation")!;
        prop.SetValue(ctx, location);
        ctx.Host.CurrentLocation = location;
        return ctx;
    }

    private static async Task<List<IItem>> ExecuteAsync(
        System.Collections.Generic.IAsyncEnumerable<IItem> stream,
        CancellationToken ct = default)
    {
        var results = new List<IItem>();
        await foreach (var item in stream.WithCancellation(ct))
            results.Add(item);
        return results;
    }

    private void AssertNoErrors()
    {
        var stream = _errors as InMemoryErrorStream;
        stream.Should().NotBeNull();
        if (stream!.RecentErrors.Count > 0)
        {
            var msgs = string.Join("\n", stream.RecentErrors.Select(e => $"{e.Category}: {e.Message}"));
            throw new Xunit.Sdk.XunitException($"Unexpected errors:\n{msgs}");
        }
    }

    private string TempPath => _tempDir.FullPath.Replace('\\', '/');

    private ItemPath TempLocation => new() { Provider = "fs", InternalPath = TempPath };

    private void CreateDir(string relativePath)
    {
        var full = Path.Combine(_tempDir.FullPath, relativePath);
        Directory.CreateDirectory(full);
    }

    private void CreateFile(string relativePath, string content = "")
    {
        var full = Path.Combine(_tempDir.FullPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ---------------------------------------------------------------------
    // cd / Set-Location 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cd_RelativeParent_NavigatesToParent()
    {
        // 准备：temp/sub，当前位置在 temp/sub
        CreateDir("sub");
        var loc = new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/sub" };
        var ctx = CreateContextAt(loc);

        // 执行：cd ..
        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse(".."));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        // 断言：当前位置应为 temp（规范化后，非 temp/sub/..）
        ctx.Host.CurrentLocation.InternalPath.Should().Be(TempPath,
            "cd .. 应规范化路径，而非存储未规范化的 temp/sub/..");
        AssertNoErrors();
    }

    [Fact]
    public async Task Cd_RelativeParentThenChild_NavigatesCorrectly()
    {
        // 准备：temp/a, temp/b，当前位置在 temp/a
        CreateDir("a");
        CreateDir("b");
        var loc = new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/a" };
        var ctx = CreateContextAt(loc);

        // 执行：cd ../b
        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse("../b"));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        ctx.Host.CurrentLocation.InternalPath.Should().Be($"{TempPath}/b");
        AssertNoErrors();
    }

    [Fact]
    public async Task Cd_Dot_StayInCurrentDir()
    {
        CreateDir("sub");
        var loc = new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/sub" };
        var ctx = CreateContextAt(loc);

        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse("."));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        // cd . 后应保持在当前目录（规范化后）
        ctx.Host.CurrentLocation.InternalPath.Should().Be($"{TempPath}/sub");
        AssertNoErrors();
    }

    [Fact]
    public async Task Cd_AbsolutePath_NavigatesToAbsolute()
    {
        CreateDir("target");
        var ctx = CreateContext();
        var absPath = $"{TempPath}/target";

        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse(absPath));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        ctx.Host.CurrentLocation.InternalPath.Should().Be(absPath.Replace('\\', '/'));
        AssertNoErrors();
    }

    [Fact]
    public async Task Cd_Subdir_NavigatesToSubdir()
    {
        CreateDir("sub");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse("sub"));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        ctx.Host.CurrentLocation.InternalPath.Should().Be($"{TempPath}/sub");
        AssertNoErrors();
    }

    [Fact]
    public async Task Cd_NonexistentDir_WritesItemNotFound()
    {
        var ctx = CreateContextAt(TempLocation);

        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse("nonexistent"));

        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        var stream = _errors as InMemoryErrorStream;
        stream.Should().NotBeNull();
        stream!.RecentErrors.Should().Contain(e => e.Category == ErrorCategory.ItemNotFound);
    }

    // ---------------------------------------------------------------------
    // pushd / Pop-Location 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Pushd_ThenPop_RestoresLocation()
    {
        CreateDir("a");
        CreateDir("b");
        var ctx = CreateContextAt(new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/a" });

        // pushd ../b
        var pushCmd = new PushLocationCommand();
        var pushArgs = new PushLocationCommand.Args(Path: ItemPath.Parse("../b"));
        await ExecuteAsync(pushCmd.ExecuteAsync(pushArgs, ctx));

        ctx.Host.CurrentLocation.InternalPath.Should().Be($"{TempPath}/b");

        // popd：需要新 ctx，CurrentLocation 设为当前位置
        var popCtx = CreateContextAt(new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/b" });
        var popCmd = new PopLocationCommand();
        var popArgs = new PopLocationCommand.Args();
        await ExecuteAsync(popCmd.ExecuteAsync(popArgs, popCtx));

        popCtx.Host.CurrentLocation.InternalPath.Should().Be($"{TempPath}/a");
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // ls / Get-ChildItem 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Ls_ListsDirectoryContents()
    {
        CreateFile("file1.txt", "content1");
        CreateFile("file2.txt", "content2");
        CreateDir("subdir");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new GetChildItemCommand();
        var args = new GetChildItemCommand.Args(Path: null, Recurse: false);
        var results = await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        var names = results.Select(r => r.Name).ToList();
        names.Should().Contain(new[] { "file1.txt", "file2.txt", "subdir" });
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // mkdir / New-Item -ItemType Directory 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Mkdir_CreatesDirectory()
    {
        var ctx = CreateContextAt(TempLocation);

        var cmd = new NewItemCommand();
        var args = new NewItemCommand.Args(
            Path: ItemPath.Parse("newdir"),
            Type: "Directory",
            Content: null);
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        Directory.Exists(Path.Combine(_tempDir.FullPath, "newdir")).Should().BeTrue();
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // rm / Remove-Item 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Rm_RemovesFile()
    {
        CreateFile("toDelete.txt", "content");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new RemoveItemCommand();
        var args = new RemoveItemCommand.Args(
            Path: ItemPath.Parse("toDelete.txt"),
            Recurse: false,
            Force: false);
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        File.Exists(Path.Combine(_tempDir.FullPath, "toDelete.txt")).Should().BeFalse();
        AssertNoErrors();
    }

    [Fact]
    public async Task Rm_Recurse_RemovesDirectory()
    {
        CreateDir("dir/sub");
        CreateFile("dir/file.txt", "content");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new RemoveItemCommand();
        var args = new RemoveItemCommand.Args(
            Path: ItemPath.Parse("dir"),
            Recurse: true,
            Force: false);
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        Directory.Exists(Path.Combine(_tempDir.FullPath, "dir")).Should().BeFalse();
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // cp / Copy-Item 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cp_CopiesFile()
    {
        CreateFile("source.txt", "hello");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new CopyItemCommand();
        var args = new CopyItemCommand.Args(
            Source: ItemPath.Parse("source.txt"),
            Destination: ItemPath.Parse("dest.txt"),
            Recurse: false,
            Force: false);
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        File.Exists(Path.Combine(_tempDir.FullPath, "dest.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_tempDir.FullPath, "dest.txt")).Should().Be("hello");
        // 源文件仍存在
        File.Exists(Path.Combine(_tempDir.FullPath, "source.txt")).Should().BeTrue();
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // mv / Move-Item 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Mv_MovesFile()
    {
        CreateFile("source.txt", "content");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new MoveItemCommand();
        var args = new MoveItemCommand.Args(
            Source: ItemPath.Parse("source.txt"),
            Destination: ItemPath.Parse("moved.txt"));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        File.Exists(Path.Combine(_tempDir.FullPath, "source.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_tempDir.FullPath, "moved.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_tempDir.FullPath, "moved.txt")).Should().Be("content");
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // cat / Get-Content 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Cat_ReadsFileContent()
    {
        CreateFile("readme.txt", "line1\nline2");
        var ctx = CreateContextAt(TempLocation);

        var cmd = new GetContentCommand();
        var args = new GetContentCommand.Args(
            Path: ItemPath.Parse("readme.txt"),
            TotalCount: null,
            Tail: null);
        // Get-Content 实现为 IPipelineSource：内容写到 Host 输出而非 yield return。
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        // 验证 Host 输出捕获了文件内容。
        _builder.CapturedOutput.Should().NotBeNull();
        _builder.CapturedOutput!.Should().Contain("line1");
        _builder.CapturedOutput!.Should().Contain("line2");
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // Set-Content / Out-File 测试
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SetContent_WritesFileContent()
    {
        var ctx = CreateContextAt(TempLocation);

        var cmd = new SetContentCommand();
        var args = new SetContentCommand.Args(
            Path: ItemPath.Parse("output.txt"),
            Value: "hello world");
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        File.Exists(Path.Combine(_tempDir.FullPath, "output.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_tempDir.FullPath, "output.txt")).Should().Contain("hello world");
        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // 综合场景：cd → ls → mkdir → cp → rm 序列
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sequence_CdLsMkdirCpRm()
    {
        var ctx = CreateContextAt(TempLocation);

        // 1. mkdir project
        var mkdirCmd = new NewItemCommand();
        var mkdirArgs = new NewItemCommand.Args(
            Path: ItemPath.Parse("project"),
            Type: "Directory",
            Content: null);
        await ExecuteAsync(mkdirCmd.ExecuteAsync(mkdirArgs, ctx));

        // 2. cd project
        var cdCmd = new SetLocationCommand();
        var cdArgs = new SetLocationCommand.Args(Path: ItemPath.Parse("project"));
        await ExecuteAsync(cdCmd.ExecuteAsync(cdArgs, ctx));
        ctx.Host.CurrentLocation.InternalPath.Should().Be($"{TempPath}/project");

        // 3. 在 project 下创建文件
        CreateFile("project/test.txt", "test content");

        // 4. cd project 后需新 ctx（CurrentLocation 是快照）
        var projectCtx = CreateContextAt(new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/project" });

        // 5. cp test.txt test_copy.txt
        var cpCmd = new CopyItemCommand();
        var cpArgs = new CopyItemCommand.Args(
            Source: ItemPath.Parse("test.txt"),
            Destination: ItemPath.Parse("test_copy.txt"),
            Recurse: false,
            Force: false);
        await ExecuteAsync(cpCmd.ExecuteAsync(cpArgs, projectCtx));

        File.Exists(Path.Combine(_tempDir.FullPath, "project", "test_copy.txt")).Should().BeTrue();

        // 6. ls（验证两个文件都在）
        var lsCmd = new GetChildItemCommand();
        var lsArgs = new GetChildItemCommand.Args(Path: null, Recurse: false);
        var lsResults = await ExecuteAsync(lsCmd.ExecuteAsync(lsArgs, projectCtx));
        var names = lsResults.Select(r => r.Name).ToList();
        names.Should().Contain(new[] { "test.txt", "test_copy.txt" });

        // 7. rm test.txt
        var rmCmd = new RemoveItemCommand();
        var rmArgs = new RemoveItemCommand.Args(
            Path: ItemPath.Parse("test.txt"),
            Recurse: false,
            Force: false);
        await ExecuteAsync(rmCmd.ExecuteAsync(rmArgs, projectCtx));

        File.Exists(Path.Combine(_tempDir.FullPath, "project", "test.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_tempDir.FullPath, "project", "test_copy.txt")).Should().BeTrue();

        // 8. cd .. 回到 temp
        var cdBackCmd = new SetLocationCommand();
        var cdBackArgs = new SetLocationCommand.Args(Path: ItemPath.Parse(".."));
        await ExecuteAsync(cdBackCmd.ExecuteAsync(cdBackArgs, projectCtx));
        projectCtx.Host.CurrentLocation.InternalPath.Should().Be(TempPath,
            "cd .. 从 project 回到 temp 应规范化路径");

        AssertNoErrors();
    }

    // ---------------------------------------------------------------------
    // 路径规范化专项测试
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("..", "sub", "")]              // temp/sub + .. → temp
    [InlineData("../sibling", "a", "sibling")] // temp/a + ../sibling → temp/sibling
    [InlineData(".", "sub", "")]               // temp/sub + . → temp/sub
    public async Task Cd_PathNormalization(string relativePath, string fromSub, string toSub)
    {
        CreateDir(fromSub);
        if (!string.IsNullOrEmpty(toSub))
            CreateDir(toSub);

        var loc = new ItemPath { Provider = "fs", InternalPath = $"{TempPath}/{fromSub}" };
        var ctx = CreateContextAt(loc);

        var cmd = new SetLocationCommand();
        var args = new SetLocationCommand.Args(Path: ItemPath.Parse(relativePath));
        await ExecuteAsync(cmd.ExecuteAsync(args, ctx));

        // 验证路径已规范化（不含 .. 或 . 段）
        ctx.Host.CurrentLocation.InternalPath.Should().NotContain("..",
            $"路径应已规范化，不应包含 '..' 段");
        ctx.Host.CurrentLocation.InternalPath.Should().NotEndWith("/.",
            $"路径应已规范化，不应以 '/.' 结尾");
        AssertNoErrors();
    }
}
