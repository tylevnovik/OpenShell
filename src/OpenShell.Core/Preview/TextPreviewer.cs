using System.Text;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 文本预览器。Per ADR-0030 §2.
/// 流式读取前 1000 行; 文件 &gt; 1MB 标记 Truncated; 前 8KB 含 \0 判定为二进制;
/// 按扩展名检测语言; 编码检测简化为 UTF-8 (BOM 由 StreamReader 自动识别)。
/// 构造函数接受内容流打开委托, 避免直接依赖 <see cref="OpenShell.Providers.IContentProvider"/>。
/// </summary>
public sealed class TextPreviewer : IPreviewer
{
    private const int MaxPreviewLines = 1000;
    private const long MaxFileSize = 1 * 1024 * 1024; // 1MB
    private const int BinaryCheckBytes = 8 * 1024;     // 8KB

    // 支持的文本扩展名 (per ADR-0030 §2 + 常见代码扩展名)。
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".xml", ".cs", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".md", ".markdown", ".log", ".yaml", ".yml", ".toml", ".sql", ".sh",
        ".csv", ".ini", ".cfg", ".conf", ".properties", ".go", ".rs", ".java",
        ".c", ".cpp", ".cc", ".h", ".hpp", ".css", ".scss", ".less",
        ".html", ".htm", ".bat", ".ps1", ".dockerfile",
    };

    // 扩展名 → 语言标识 (用于语法高亮, TextMate 标识符风格)。
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".py"] = "python",
        [".js"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".jsx"] = "javascript",
        [".json"] = "json",
        [".xml"] = "xml",
        [".md"] = "markdown",
        [".markdown"] = "markdown",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".toml"] = "toml",
        [".sql"] = "sql",
        [".sh"] = "shell",
        [".csv"] = "csv",
        [".ini"] = "ini",
        [".cfg"] = "ini",
        [".conf"] = "ini",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".c"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".h"] = "c",
        [".hpp"] = "cpp",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".html"] = "html",
        [".htm"] = "html",
        [".bat"] = "bat",
        [".ps1"] = "powershell",
    };

    private readonly Func<ItemPath, CancellationToken, Task<Stream>> _openRead;

    public TextPreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead)
    {
        _openRead = openRead;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        var ext = GetExtension(item.Path);
        return SupportedExtensions.Contains(ext);
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        Stream stream = await _openRead(item.Path, ct).ConfigureAwait(false);
        try
        {
            // 二进制检测: 读前 8KB, 若含 \0 判定为二进制。
            var probe = new byte[BinaryCheckBytes];
            int probeRead = 0;
            while (probeRead < BinaryCheckBytes)
            {
                var n = await stream.ReadAsync(probe.AsMemory(probeRead, BinaryCheckBytes - probeRead), ct).ConfigureAwait(false);
                if (n == 0) break;
                probeRead += n;
            }

            for (int i = 0; i < probeRead; i++)
            {
                if (probe[i] == 0)
                    return new PreviewViewModel.NotSupported("Binary file");
            }

            // 构造后续读取流: seekable 直接 reset, 否则用 ConcatStream 拼接预读字节。
            Stream readStream;
            if (stream.CanSeek)
            {
                stream.Position = 0;
                readStream = stream;
            }
            else
            {
                var prefix = new MemoryStream(probe, 0, probeRead, writable: false);
                readStream = new ConcatStream(prefix, stream);
            }

            // 大文件标记截断 (per ADR-0030 §2: 文件 > 1MB 不全加载)。
            var truncated = item.Size is { } size && size > MaxFileSize;

            using var reader = new StreamReader(readStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sb = new StringBuilder();
            var previewLines = 0;
            var totalLines = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                totalLines++;
                if (previewLines < MaxPreviewLines)
                {
                    if (previewLines > 0) sb.Append('\n');
                    sb.Append(line);
                    previewLines++;
                }
            }

            var language = GetLanguage(item.Path);
            return new PreviewViewModel.Text(sb.ToString(), language, totalLines, truncated);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string GetExtension(ItemPath path)
    {
        var name = path.GetName();
        var idx = name.LastIndexOf('.');
        return idx >= 0 ? name[idx..] : "";
    }

    private static string? GetLanguage(ItemPath path)
        => LanguageMap.TryGetValue(GetExtension(path), out var lang) ? lang : null;
}
