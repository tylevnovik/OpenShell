using OpenShell.Items;

namespace OpenShell.Preview;

/// <summary>
/// 单个预览器抽象。Per ADR-0030 §2.
/// 每个预览器负责一种类型 (文本 / 图片 / PDF / 视频 / 归档)。
/// <see cref="IPreviewService"/> 按 <see cref="CanPreview"/> 顺序选择第一个支持的预览器。
/// </summary>
public interface IPreviewer
{
    /// <summary>是否能预览此项。</summary>
    bool CanPreview(IItem item);

    /// <summary>异步生成预览视图模型。返回 null 表示无法生成。</summary>
    ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct);
}
