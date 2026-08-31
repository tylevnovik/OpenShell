using OpenShell.Items;
using OpenShell.Paths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace OpenShell.Preview;

/// <summary>
/// 图片预览器。Per ADR-0030 §2。
/// 支持格式: PNG / JPEG / GIF / BMP / WEBP (per ADR-0030 §2)。
/// IH-009: 引入 <c>SixLabors.ImageSharp</c> (纯托管, 无原生二进制) 解码上述全部格式,
/// 统一转码为 PNG 字节交给 GUI; SVG 仍需矢量渲染引擎, 显式返回 NotSupported。
/// 实现要点:
/// <list type="bullet">
///   <item>输入字节超过 <see cref="MaxInputBytes"/> 时不解码, 直接 NotSupported (防内存放大)。</item>
///   <item>解码后最长边超过 <see cref="MaxEdgePixels"/> 时按比例缩放为缩略图 (安全缩略)。</item>
///   <item>无法识别 / 损坏的文件返回 <see cref="PreviewViewModel.NotSupported"/> 并附原因, 不抛异常。</item>
/// </list>
/// </summary>
public sealed class ImagePreviewer : IPreviewer
{
    /// <summary>输入文件字节上限 (默认 64MB); 超过则拒绝解码以避免内存放大。</summary>
    public const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>解码后最长边像素上限; 超过则等比缩放到该值 (缩略图)。</summary>
    public const int MaxEdgePixels = 4096;

    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    private readonly Func<ItemPath, CancellationToken, Task<Stream>> _openRead;
    private readonly long _maxInputBytes;

    public ImagePreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead)
        : this(openRead, MaxInputBytes)
    {
    }

    /// <summary>可注入输入上限的重载 (测试用于验证资源上限行为)。</summary>
    public ImagePreviewer(Func<ItemPath, CancellationToken, Task<Stream>> openRead, long maxInputBytes)
    {
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _maxInputBytes = maxInputBytes > 0 ? maxInputBytes : MaxInputBytes;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        if (item.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        var ext = GetExtension(item.Path);
        return RasterExtensions.Contains(ext)
            || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        // SVG 需要矢量渲染引擎 (ImageSharp 不解码 SVG), 明确降级而非静默失败。
        if (GetExtension(item.Path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return new PreviewViewModel.NotSupported("SVG preview is not supported (vector rendering not available).");
        }

        // 读入字节并做大小上限保护 (防恶意/超大文件内存放大)。
        byte[] bytes;
        await using (var stream = await _openRead(item.Path, ct).ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > _maxInputBytes)
                {
                    return new PreviewViewModel.NotSupported(
                        $"Image exceeds preview size limit ({_maxInputBytes / (1024 * 1024)}MB).");
                }
                ms.Write(buffer, 0, n);
            }
            bytes = ms.ToArray();
        }

        if (bytes.Length == 0)
            return new PreviewViewModel.NotSupported("Image file is empty.");

        try
        {
            using var image = Image.Load(bytes);
            ct.ThrowIfCancellationRequested();

            // 安全缩略: 最长边超过上限时按比例缩放, 控制解码后内存与渲染开销。
            var longest = Math.Max(image.Width, image.Height);
            if (longest > MaxEdgePixels)
            {
                var target = new Size(
                    Math.Max(1, (int)Math.Round(image.Width * (MaxEdgePixels / (double)longest))),
                    Math.Max(1, (int)Math.Round(image.Height * (MaxEdgePixels / (double)longest))));
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = target,
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            using var outStream = new MemoryStream();
            image.Save(outStream, new PngEncoder());
            return new PreviewViewModel.Image(outStream.ToArray(), image.Width, image.Height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            // 无法识别 / 损坏: 明确降级并给出原因, 不向调用方抛异常。
            return new PreviewViewModel.NotSupported($"Unable to decode image: {ex.Message}");
        }
    }

    private static string GetExtension(ItemPath path)
    {
        var name = path.GetName();
        var idx = name.LastIndexOf('.');
        return idx >= 0 ? name[idx..] : "";
    }
}
