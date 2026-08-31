using OpenShell.Items;

namespace OpenShell.Preview;

/// <summary>
/// 预览面板服务抽象。Per ADR-0030 §1.
/// 协调多个 <see cref="IPreviewer"/> 生成预览视图模型。预览生成必须有超时 (默认 5s), 见 ADR-0030 约束。
/// </summary>
public interface IPreviewService
{
    /// <summary>是否能预览。</summary>
    bool CanPreview(IItem item);

    /// <summary>异步生成预览视图模型。返回 null 表示无 previewer 可处理。</summary>
    ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct = default);
}

/// <summary>预览生成选项。Per ADR-0030 §1.</summary>
public sealed record PreviewOptions(int MaxWidth = 400, int MaxHeight = 300, bool WithMetadata = true);

/// <summary>
/// 预览视图模型。Per ADR-0030 §1.
/// 注意: ADR 中的 Image/Pdf/Video 类型依赖 Avalonia IBitmap / 外部库, 本次简化:
/// - Image 用 <see cref="Image.PngData"/> (byte[]) 避免依赖 Avalonia IBitmap
/// - Pdf 用提取的纯文本 + 估算页数 (轻量 PDF stream parser, 不依赖 PDFium)
/// - Video 用 <see cref="Video.Duration"/> + <see cref="Video.Metadata"/> 文本 (依赖 ffprobe, 不可用时 Metadata=null)
/// </summary>
public abstract record PreviewViewModel
{
    /// <summary>文本预览: 内容 + 语言 + 总行数 + 是否截断 (大文件仅前 1000 行)。</summary>
    public sealed record Text(string Content, string? Language, int TotalLines, bool Truncated) : PreviewViewModel;

    /// <summary>图片预览: PNG 编码的字节数据 + 原始宽高。</summary>
    public sealed record Image(byte[] PngData, int Width, int Height) : PreviewViewModel;

    /// <summary>归档预览: 包内条目列表 (前 100 个)。</summary>
    public sealed record Archive(IReadOnlyList<IItem> Entries) : PreviewViewModel;

    /// <summary>
    /// PDF 预览: 从 stream objects 提取的文本 (前 N 页) + 估算页数。
    /// 限制: 仅解析 BT/ET 文本块中的 Tj/TJ 操作符, 不渲染矢量/位图; 复杂 PDF 可能提取不到文本。
    /// </summary>
    public sealed record Pdf(string ExtractedText, int EstimatedPageCount) : PreviewViewModel;

    /// <summary>
    /// 视频预览: 元数据 (时长 / 编码 / 分辨率); ffprobe 不可用时 Metadata=null。
    /// IH-009: 可选缩略图 (ffmpeg 提取首帧转 PNG); ffmpeg 不可用时 ThumbnailPng=null, 保持纯元数据降级。
    /// </summary>
    public sealed record Video(
        TimeSpan? Duration,
        string? Metadata,
        byte[]? ThumbnailPng = null,
        int ThumbnailWidth = 0,
        int ThumbnailHeight = 0) : PreviewViewModel;

    /// <summary>代码预览: 前 200 行带基础语法高亮 token (keyword/comment/string 分类) + 语言。</summary>
    public sealed record Code(string HighlightedContent, string Language, int TotalLines, bool Truncated) : PreviewViewModel;

    /// <summary>不支持预览: 给出原因 (二进制 / 无 previewer / 解码失败)。</summary>
    public sealed record NotSupported(string Reason) : PreviewViewModel;
}
