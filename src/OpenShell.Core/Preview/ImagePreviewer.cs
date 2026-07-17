using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 图片预览器。Per ADR-0030 §2.
/// 支持格式: PNG / JPEG / GIF / BMP / WEBP / SVG (per ADR-0030 §2)。
/// 实现限制 (per ADR-0030 §2 + 任务约束):
/// <list type="bullet">
///   <item>未引用 SkiaSharp / System.Drawing.Common, 因此仅 PNG 文件能直接返回字节流 (已是 PNG 编码)。</item>
///   <item>JPEG / GIF / BMP / WEBP / SVG 需 SkiaSharp 解码 → 暂返回 NotSupported, 提示需添加 SkiaSharp 包。</item>
///   <item>未来集成: 在 OpenShell.Core.csproj 添加 <c>SkiaSharp</c> 后, 可在此处用 <c>SKBitmap.Decode(stream)</c> 解码并 <c>SKBitmap.Encode(SKEncodedImageFormat.Png, 100)</c> 转 PNG。</item>
/// </list>
/// 大图缩放: 当前未实现 (需 SkiaSharp <c>SKBitmap.Resize</c>)。PNG 直接返回原始字节。
/// </summary>
public sealed class ImagePreviewer : IPreviewer
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg",
    };

    // PNG 文件签名 (8 字节)。
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Func<ItemPath, CancellationToken, Task<Stream>> _openRead;

    public ImagePreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead)
    {
        _openRead = openRead;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        if (item.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return ImageExtensions.Contains(GetExtension(item.Path));
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        // 仅支持 PNG 直接读取 (无需解码); 其他格式需 SkiaSharp (per 任务约束: 不添加重依赖)。
        var ext = GetExtension(item.Path);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return new PreviewViewModel.NotSupported(
                "Image decoding requires SkiaSharp (not referenced). PNG files are decoded natively.");
        }

        await using var stream = await _openRead(item.Path, ct).ConfigureAwait(false);

        // 读 PNG 签名 + IHDR (前 24 字节) 获取宽高 (per PNG spec)。
        // PNG layout: 8 字节签名 + 4 字节长度 + 4 字节 "IHDR" + 4 字节 width + 4 字节 height + ...
        var header = new byte[24];
        var read = 0;
        while (read < header.Length)
        {
            var n = await stream.ReadAsync(header.AsMemory(read, header.Length - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        if (read < 24)
            return new PreviewViewModel.NotSupported("PNG file too small to contain IHDR.");

        // 校验签名。
        for (int i = 0; i < PngSignature.Length; i++)
        {
            if (header[i] != PngSignature[i])
                return new PreviewViewModel.NotSupported("Not a valid PNG file (signature mismatch).");
        }

        // IHDR 在 offset 12, "IHDR" 在 offset 12, width 在 offset 16, height 在 offset 20 (big-endian)。
        var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

        // 重置流位置并读取完整文件字节作为 PngData。
        if (stream.CanSeek)
        {
            stream.Position = 0;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return new PreviewViewModel.Image(ms.ToArray(), width, height);
        }
        else
        {
            // 非 seekable: 把已读 header 拼到剩余流。
            var prefix = new MemoryStream(header, 0, read, writable: false);
            using var concat = new ConcatStream(prefix, stream);
            using var ms = new MemoryStream();
            await concat.CopyToAsync(ms, ct).ConfigureAwait(false);
            return new PreviewViewModel.Image(ms.ToArray(), width, height);
        }
    }

    private static string GetExtension(ItemPath path)
    {
        var name = path.GetName();
        var idx = name.LastIndexOf('.');
        return idx >= 0 ? name[idx..] : "";
    }
}
