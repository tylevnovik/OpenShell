using System.Text;
using System.Text.RegularExpressions;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// PDF 预览器。Per ADR-0030 §2.
/// 实现限制 (per 任务约束: 不添加重依赖):
/// <list type="bullet">
///   <item>未引用 PDFium / PdfiumViewer / SkiaSharp PDF 支持, 采用轻量 PDF stream parser: 仅解析 BT/ET 文本块中的 Tj/TJ 操作符。</item>
///   <item>复杂 PDF (含矢量图 / 嵌入位图 / 加密 / 自定义字体编码) 可能提取不到文本。</item>
///   <item>页数估算: 计数 "/Type /Page" 出现次数 (per PDF spec 7.7.2 Pages Tree)。</item>
///   <item>完整 PDF 渲染需 M5+ 评估 PDFium 集成。</item>
/// </list>
/// </summary>
public sealed class PdfPreviewer : IPreviewer
{
    private const long MaxPdfSize = 50 * 1024 * 1024; // 50MB
    private const int MaxExtractedChars = 20_000;

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
    };

    private readonly Func<ItemPath, CancellationToken, Task<Stream>> _openRead;

    public PdfPreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead)
    {
        _openRead = openRead;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        if (item.ContentType?.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return PdfExtensions.Contains(GetExtension(item.Path));
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        // 大文件不全加载 (per ADR-0030 §2: 大文件流式预览)。
        if (item.Size is { } size && size > MaxPdfSize)
        {
            return new PreviewViewModel.NotSupported($"PDF exceeds preview size limit ({MaxPdfSize / (1024 * 1024)}MB).");
        }

        await using var stream = await _openRead(item.Path, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        var bytes = ms.ToArray();

        // PDF 文件应以 "%PDF-" 开头 (per PDF spec 7.5.2 File Header)。
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
        {
            return new PreviewViewModel.NotSupported("Not a valid PDF file (header missing).");
        }

        // 用 Latin1 解码以保留字节级正确性 (PDF 内容流可能含非 UTF-8 字节)。
        var content = Encoding.Latin1.GetString(bytes);

        // 1. 估算页数: 计数 /Type /Page (排除 /Pages)。
        var pageCount = EstimatePageCount(content);

        // 2. 提取文本: 仅解析 BT/ET 块中的 Tj / TJ 操作符。
        var extracted = ExtractText(content);

        if (extracted.Length == 0)
        {
            return new PreviewViewModel.NotSupported(
                "PDF text extraction failed (likely contains images or encoded fonts). Consider adding PDFium for full rendering.");
        }

        var truncated = false;
        if (extracted.Length > MaxExtractedChars)
        {
            extracted = extracted[..MaxExtractedChars];
            truncated = true;
        }

        // 用 Text 变体承载提取的文本, language=pdf 以便 GUI 选用合适渲染。
        return new PreviewViewModel.Text(
            Content: extracted + (truncated ? "\n\n[... truncated ...]" : ""),
            Language: "pdf",
            TotalLines: CountLines(extracted),
            Truncated: truncated);
    }

    /// <summary>估算页数: 计数 "/Type /Page" (排除 "/Type /Pages")。Per PDF spec 7.7.2.</summary>
    private static int EstimatePageCount(string content)
    {
        var count = 0;
        var idx = 0;
        while ((idx = content.IndexOf("/Type", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // 跳过空白。
            var p = idx + 5;
            while (p < content.Length && char.IsWhiteSpace(content[p])) p++;
            if (p + 4 <= content.Length && content.AsSpan(p, 4).Equals("/Page".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                // 排除 "/Pages"。
                if (p + 5 > content.Length || content[p + 5] != 's')
                {
                    count++;
                }
            }
            idx = p;
        }
        return count;
    }

    /// <summary>
    /// 提取 BT/ET 文本块中的 Tj / TJ 操作符的字符串参数。Per PDF spec 9.4 Text Objects.
    /// 仅匹配 ASCII 字符串 "(...)" 和十六进制字符串 "&lt;...&gt;", 不解压 FlateDecode 压缩流。
    /// </summary>
    private static string ExtractText(string content)
    {
        var sb = new StringBuilder();
        var btRegex = new Regex(@"BT(.*?)ET", RegexOptions.Singleline | RegexOptions.Compiled);
        // Tj: (text) Tj  |  TJ: [(t1) -100 (t2) ...] TJ
        var tjRegex = new Regex(@"\((?:[^()\\]|\\.)*\)", RegexOptions.Compiled);
        var hexRegex = new Regex(@"<(?:[0-9A-Fa-f]{2})+>", RegexOptions.Compiled);

        foreach (Match bt in btRegex.Matches(content))
        {
            var block = bt.Value;
            foreach (Match s in tjRegex.Matches(block))
            {
                sb.Append(UnescapePdfString(s.Value.Substring(1, s.Value.Length - 2)));
            }
            foreach (Match h in hexRegex.Matches(block))
            {
                sb.Append(HexToString(h.Value.Substring(1, h.Value.Length - 2)));
            }
        }

        return sb.ToString();
    }

    /// <summary>反转义 PDF 字符串 (per PDF spec 7.3.4.2 Literal Strings): \n \r \t \b \f \( \) \\ \ddd。</summary>
    private static string UnescapePdfString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            if (i + 1 >= s.Length) { sb.Append('\\'); break; }
            var next = s[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '(': sb.Append('('); break;
                case ')': sb.Append(')'); break;
                case '\\': sb.Append('\\'); break;
                case '\n': break; // line continuation
                case '\r':
                    if (i + 1 < s.Length && s[i + 1] == '\n') i++;
                    break;
                default:
                    // \ddd 八进制
                    if (next >= '0' && next <= '7')
                    {
                        var octal = next.ToString();
                        for (int k = 0; k < 2 && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7'; k++)
                            octal += s[++i];
                        sb.Append((char)Convert.ToInt32(octal, 8));
                    }
                    else
                    {
                        sb.Append('\\').Append(next);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>PDF 十六进制字符串 → 文本。Per PDF spec 7.3.4.3 Hexadecimal Strings。</summary>
    private static string HexToString(string hex)
    {
        if (hex.Length % 2 != 0) hex += "0";
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        // 假定 UTF-8 (PDF 1.7+ 支持 UTF-8 字符串 via UTF-16BE BOM, 此处简化)。
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var count = 1;
        foreach (var ch in s) if (ch == '\n') count++;
        return count;
    }

    private static string GetExtension(ItemPath path)
    {
        var name = path.GetName();
        var idx = name.LastIndexOf('.');
        return idx >= 0 ? name[idx..] : "";
    }
}
