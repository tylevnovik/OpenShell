using FluentAssertions;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers.FileSystem;
using OpenShell.TestUtils;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;
using Xunit;

namespace OpenShell.Providers.FileSystem.Tests;

/// <summary>
/// FileSystemProvider 具体行为测试。Per ADR-0001, ADR-0033.
/// 用 TempDir 隔离文件系统, 验证 GetItem / GetChildren / GetContent / SetContent / CreateDirectory / Delete / Rename 等行为。
/// </summary>
public class FileSystemProviderTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FileSystemProvider _provider = new();

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    private ItemPath FsPath(string relativePath)
    {
        var full = System.IO.Path.Combine(_tempDir.FullPath, relativePath).Replace('\\', '/');
        return new ItemPath { Provider = "fs", InternalPath = full };
    }

    [Fact]
    public async Task GetItemAsync_ExistingFile_ReturnsItemWithCorrectName()
    {
        _tempDir.CreateFile("test.txt", "hello");

        var item = await _provider.GetItemAsync(FsPath("test.txt"));

        item.Should().NotBeNull();
        item!.Name.Should().Be("test.txt");
        item.Kind.Should().Be(ItemKind.File);
    }

    [Fact]
    public async Task GetItemAsync_Nonexistent_ReturnsNull()
    {
        var item = await _provider.GetItemAsync(FsPath("does-not-exist.txt"));
        item.Should().BeNull();
    }

    [Fact]
    public async Task GetItemAsync_Directory_ReturnsDirectoryKind()
    {
        _tempDir.CreateDirectory("subdir");

        var item = await _provider.GetItemAsync(FsPath("subdir"));

        item.Should().NotBeNull();
        item!.Kind.Should().Be(ItemKind.Directory);
    }

    [Fact]
    public async Task GetItemAsync_File_ReturnsFileSize()
    {
        _tempDir.CreateFile("size.txt", "12345");

        var item = await _provider.GetItemAsync(FsPath("size.txt"));

        item.Should().NotBeNull();
        item!.Size.Should().Be(5);
    }

    [Fact]
    public async Task GetItemAsync_PopulatesTimestamps()
    {
        _tempDir.CreateFile("timestamps.txt", "x");

        var item = await _provider.GetItemAsync(FsPath("timestamps.txt"));

        item.Should().NotBeNull();
        item!.Timestamps.Should().NotBe(ItemTimestamps.None);
    }

    [Fact]
    public async Task GetChildrenAsync_ListsFilesAndDirectories()
    {
        _tempDir.CreateFile("a.txt", "a");
        _tempDir.CreateFile("b.txt", "b");
        _tempDir.CreateDirectory("subdir");

        var root = FsPath("");
        var children = new List<IItem>();
        await foreach (var child in _provider.GetChildrenAsync(root, new EnumerationOptions()))
            children.Add(child);

        children.Should().HaveCount(3);
        children.Should().Contain(c => c.Name == "a.txt");
        children.Should().Contain(c => c.Name == "b.txt");
        children.Should().Contain(c => c.Name == "subdir" && c.Kind == ItemKind.Directory);
    }

    [Fact]
    public async Task GetChildrenAsync_Nonexistent_ReturnsEmpty()
    {
        var children = new List<IItem>();
        await foreach (var c in _provider.GetChildrenAsync(FsPath("does-not-exist"), new EnumerationOptions()))
            children.Add(c);

        children.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChildrenAsync_WithFilter_ReturnsMatchingFiles()
    {
        _tempDir.CreateFile("a.txt", "a");
        _tempDir.CreateFile("b.log", "b");
        _tempDir.CreateFile("c.txt", "c");

        var children = new List<IItem>();
        await foreach (var c in _provider.GetChildrenAsync(
            FsPath(""),
            new EnumerationOptions { Filter = "*.txt" }))
            children.Add(c);

        children.Should().HaveCount(2);
        children.Should().OnlyContain(c => c.Name.EndsWith(".txt"));
    }

    [Fact]
    public async Task GetChildrenAsync_WithRecurse_ReturnsNestedChildren()
    {
        _tempDir.CreateFile("top.txt", "t");
        _tempDir.CreateFile("nested/deep.txt", "d");

        var children = new List<IItem>();
        await foreach (var c in _provider.GetChildrenAsync(
            FsPath(""),
            new EnumerationOptions { Recurse = true }))
            children.Add(c);

        children.Should().Contain(c => c.Name == "top.txt");
        children.Should().Contain(c => c.Name == "deep.txt");
    }

    [Fact]
    public async Task GetContentAsync_ReadsFileContent()
    {
        _tempDir.CreateFile("read.txt", "hello world");

        await using var stream = await _provider.OpenReadAsync(FsPath("read.txt"));
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        content.Should().Be("hello world");
    }

    [Fact]
    public async Task SetContentAsync_WritesFileContent()
    {
        var path = FsPath("write.txt");
        await using (var stream = await _provider.OpenWriteAsync(path))
        using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync("written content");
            await writer.FlushAsync();
        }

        _tempDir.FileExists("write.txt").Should().BeTrue();
        System.IO.File.ReadAllText(_tempDir.GetFullPath("write.txt")).Should().Be("written content");
    }

    [Fact]
    public async Task SetContentAsync_CreatesParentDirectoryIfNeeded()
    {
        // OpenWriteAsync 应自动创建不存在的父目录 (FileSystemProvider 实现)。
        var path = FsPath("new/nested/file.txt");
        await using (var stream = await _provider.OpenWriteAsync(path))
        using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync("data");
            await writer.FlushAsync();
        }

        _tempDir.FileExists("new/nested/file.txt").Should().BeTrue();
    }

    [Fact]
    public async Task CreateDirectoryAsync_CreatesDirectory()
    {
        await _provider.CreateDirectoryAsync(FsPath("newdir"));

        _tempDir.DirectoryExists("newdir").Should().BeTrue();
    }

    [Fact]
    public async Task CreateDirectoryAsync_Nested_CreatesAllAncestors()
    {
        await _provider.CreateDirectoryAsync(FsPath("a/b/c"));

        _tempDir.DirectoryExists("a/b/c").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_File_RemovesFile()
    {
        _tempDir.CreateFile("delete.txt", "x");
        _tempDir.FileExists("delete.txt").Should().BeTrue();

        await _provider.DeleteAsync(FsPath("delete.txt"), recurse: false);

        _tempDir.FileExists("delete.txt").Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_DirectoryNonRecursive_Throws()
    {
        _tempDir.CreateDirectory("nonempty/sub");
        _tempDir.CreateFile("nonempty/file.txt", "x");

        var act = async () => await _provider.DeleteAsync(FsPath("nonempty"), recurse: false);

        // 非递归删除非空目录应抛 IOException。
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task DeleteAsync_DirectoryRecursive_DeletesAll()
    {
        _tempDir.CreateDirectory("rmd/sub");
        _tempDir.CreateFile("rmd/file.txt", "x");
        _tempDir.CreateFile("rmd/sub/deep.txt", "y");

        await _provider.DeleteAsync(FsPath("rmd"), recurse: true);

        _tempDir.DirectoryExists("rmd").Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_Nonexistent_ThrowsFileNotFoundException()
    {
        var act = async () => await _provider.DeleteAsync(FsPath("missing.txt"), recurse: false);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task RenameAsync_File_RenamesToNewName()
    {
        _tempDir.CreateFile("old.txt", "data");

        await _provider.RenameAsync(FsPath("old.txt"), "new.txt");

        _tempDir.FileExists("old.txt").Should().BeFalse();
        _tempDir.FileExists("new.txt").Should().BeTrue();
    }

    [Fact]
    public async Task RenameAsync_PreservesContent()
    {
        _tempDir.CreateFile("orig.txt", "preserve-me");

        await _provider.RenameAsync(FsPath("orig.txt"), "renamed.txt");

        System.IO.File.ReadAllText(_tempDir.GetFullPath("renamed.txt")).Should().Be("preserve-me");
    }

    [Fact]
    public async Task RenameAsync_Directory_RenamesDirectory()
    {
        _tempDir.CreateDirectory("olddir");
        _tempDir.CreateFile("olddir/file.txt", "x");

        await _provider.RenameAsync(FsPath("olddir"), "newdir");

        _tempDir.DirectoryExists("olddir").Should().BeFalse();
        _tempDir.DirectoryExists("newdir").Should().BeTrue();
        _tempDir.FileExists("newdir/file.txt").Should().BeTrue();
    }

    [Fact]
    public async Task RenameAsync_Nonexistent_ThrowsFileNotFoundException()
    {
        var act = async () => await _provider.RenameAsync(FsPath("ghost.txt"), "new.txt");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task IsValidPath_FsProvider_ReturnsTrue()
    {
        _provider.IsValidPath(FsPath("anything")).Should().BeTrue();
    }

    [Fact]
    public void IsValidPath_NonFsProvider_ReturnsFalse()
    {
        var path = new ItemPath { Provider = "other", InternalPath = "x" };
        _provider.IsValidPath(path).Should().BeFalse();
    }

    [Fact]
    public void NormalizePath_ReturnsAbsolutePath()
    {
        var path = FsPath("file.txt");
        var normalized = _provider.NormalizePath(path);

        normalized.InternalPath.Should().EndWith("file.txt");
        normalized.Provider.Should().Be("fs");
    }

    [Fact]
    public async Task CanWriteAsync_ExistingWritableFile_ReturnsTrue()
    {
        _tempDir.CreateFile("writable.txt", "x");
        var canWrite = await _provider.CanWriteAsync(FsPath("writable.txt"));
        canWrite.Should().BeTrue();
    }

    [Fact]
    public async Task CanWriteAsync_NonexistentFileInExistingDir_ReturnsTrue()
    {
        var canWrite = await _provider.CanWriteAsync(FsPath("new-file.txt"));
        canWrite.Should().BeTrue();
    }

    [Fact]
    public async Task SetTimestampsAsync_UpdatesModifiedTime()
    {
        _tempDir.CreateFile("ts.txt", "x");
        var newTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await _provider.SetTimestampsAsync(FsPath("ts.txt"), newTime, newTime);

        var fi = new FileInfo(_tempDir.GetFullPath("ts.txt"));
        fi.LastWriteTimeUtc.Should().Be(newTime.UtcDateTime);
    }

    [Fact]
    public async Task GetPropertiesAsync_ReturnsAttributes()
    {
        _tempDir.CreateFile("props.txt", "x");
        var item = await _provider.GetItemAsync(FsPath("props.txt"));
        item.Should().NotBeNull();

        var bag = await _provider.GetPropertiesAsync(item!);

        bag.Values.Should().NotBeEmpty();
        bag.Values.Should().ContainKey("attributes");
    }
}
