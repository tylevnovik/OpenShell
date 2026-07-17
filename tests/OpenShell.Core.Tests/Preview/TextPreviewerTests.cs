using System.Text;
using FluentAssertions;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Preview;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Preview;

/// <summary>
/// ADR-0030 §2: TextPreviewer 单测。
/// 验证: CanPreview 扩展名判断, 小文件完整读取, 大文件 Truncated, 二进制 NotSupported, 语言检测。
/// </summary>
public class TextPreviewerTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    private TextPreviewer CreatePreviewer()
    {
        return new TextPreviewer((path, ct) =>
        {
            var fsPath = path.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
            return Task.FromResult<Stream>(new FileStream(
                fsPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192, useAsync: true));
        });
    }

    private ItemPath WriteFile(string name, string content)
    {
        var full = _tempDir.CreateFile(name, content);
        return new ItemPath { Provider = "fs", InternalPath = full.Replace('\\', '/') };
    }

    [Fact]
    public void CanPreview_TextFile_ReturnsTrue()
    {
        var path = WriteFile("a.txt", "hello");
        var item = Item.File(path);
        var previewer = CreatePreviewer();

        previewer.CanPreview(item).Should().BeTrue();
    }

    [Fact]
    public void CanPreview_BinaryExtension_ReturnsFalse()
    {
        var path = WriteFile("a.bin", "not really binary");
        var item = Item.File(path);
        var previewer = CreatePreviewer();

        previewer.CanPreview(item).Should().BeFalse();
    }

    [Fact]
    public void CanPreview_Directory_ReturnsFalse()
    {
        var dir = _tempDir.CreateDirectory("sub");
        var path = new ItemPath { Provider = "fs", InternalPath = dir.Replace('\\', '/') };
        var item = Item.Directory(path);
        var previewer = CreatePreviewer();

        previewer.CanPreview(item).Should().BeFalse();
    }

    [Fact]
    public async Task CreatePreviewAsync_SmallFile_ReturnsFullContentAndTotalLines()
    {
        var content = "line1\nline2\nline3";
        var path = WriteFile("small.txt", content);
        var item = Item.File(path, size: content.Length);
        var previewer = CreatePreviewer();

        var vm = await previewer.CreatePreviewAsync(item, new PreviewOptions(), default);

        vm.Should().NotBeNull();
        var text = vm.Should().BeOfType<PreviewViewModel.Text>().Subject;
        text.Content.Should().Be(content);
        text.TotalLines.Should().Be(3);
        text.Truncated.Should().BeFalse();
        text.Language.Should().BeNull();
    }

    [Fact]
    public async Task CreatePreviewAsync_LargeFile_SetsTruncatedTrue()
    {
        // 1.2MB 文件 (> 1MB 阈值), 2000 行, 每行 ~640 字节。
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
            sb.Append(new string('x', 630)).Append('\n');
        var content = sb.ToString();
        var path = WriteFile("large.txt", content);
        var item = Item.File(path, size: content.Length);
        var previewer = CreatePreviewer();

        var vm = await previewer.CreatePreviewAsync(item, new PreviewOptions(), default);

        vm.Should().NotBeNull();
        var text = vm.Should().BeOfType<PreviewViewModel.Text>().Subject;
        text.Truncated.Should().BeTrue();
        // 预览限制前 1000 行。
        text.TotalLines.Should().Be(2000);
        var lineCount = text.Content.Count(c => c == '\n');
        lineCount.Should().Be(999); // 前 1000 行间有 999 个换行。
    }

    [Fact]
    public async Task CreatePreviewAsync_BinaryFile_ReturnsNotSupported()
    {
        // 前 8KB 含 \0 判定为二进制。
        var bytes = new byte[100];
        bytes[10] = 0; // null byte
        var full = System.IO.Path.Combine(_tempDir.FullPath, "binary.txt");
        await System.IO.File.WriteAllBytesAsync(full, bytes);
        var path = new ItemPath { Provider = "fs", InternalPath = full.Replace('\\', '/') };
        var item = Item.File(path, size: bytes.Length);
        var previewer = CreatePreviewer();

        var vm = await previewer.CreatePreviewAsync(item, new PreviewOptions(), default);

        vm.Should().NotBeNull();
        var notSupported = vm.Should().BeOfType<PreviewViewModel.NotSupported>().Subject;
        notSupported.Reason.Should().Contain("Binary");
    }

    [Fact]
    public async Task CreatePreviewAsync_CSharpFile_DetectsCSharpLanguage()
    {
        var path = WriteFile("Program.cs", "namespace Foo;\npublic class Bar { }");
        var item = Item.File(path, size: 40);
        var previewer = CreatePreviewer();

        var vm = await previewer.CreatePreviewAsync(item, new PreviewOptions(), default);

        vm.Should().NotBeNull();
        var text = vm.Should().BeOfType<PreviewViewModel.Text>().Subject;
        text.Language.Should().Be("csharp");
    }

    [Fact]
    public async Task CreatePreviewAsync_PythonFile_DetectsPythonLanguage()
    {
        var path = WriteFile("app.py", "print('hi')");
        var item = Item.File(path, size: 12);
        var previewer = CreatePreviewer();

        var vm = await previewer.CreatePreviewAsync(item, new PreviewOptions(), default);

        vm.Should().NotBeNull();
        var text = vm.Should().BeOfType<PreviewViewModel.Text>().Subject;
        text.Language.Should().Be("python");
    }

    [Fact]
    public async Task CreatePreviewAsync_UnsupportedExtension_ReturnsNull()
    {
        var path = WriteFile("archive.zip", "PK");
        var item = Item.File(path, size: 2);
        var previewer = CreatePreviewer();

        var vm = await previewer.CreatePreviewAsync(item, new PreviewOptions(), default);

        vm.Should().BeNull();
    }

    public void Dispose() => _tempDir.Dispose();
}
