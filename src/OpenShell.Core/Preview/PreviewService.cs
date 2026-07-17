using OpenShell.Items;

namespace OpenShell.Preview;

/// <summary>
/// 预览服务协调器。Per ADR-0030 §1 / §2.
/// 持有一组 <see cref="IPreviewer"/>, <see cref="CanPreview"/> 任一支持即返回 true,
/// <see cref="CreatePreviewAsync"/> 找第一个支持的 previewer 调用之; 都不支持返回 <see cref="PreviewViewModel.NotSupported"/>。
/// </summary>
public sealed class PreviewService : IPreviewService
{
    private readonly IReadOnlyList<IPreviewer> _previewers;

    public PreviewService(IReadOnlyList<IPreviewer> previewers)
    {
        _previewers = previewers;
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        foreach (var p in _previewers)
        {
            if (p.CanPreview(item)) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct = default)
    {
        foreach (var p in _previewers)
        {
            if (p.CanPreview(item))
                return await p.CreatePreviewAsync(item, options, ct).ConfigureAwait(false);
        }
        return new PreviewViewModel.NotSupported("No previewer supports this item.");
    }
}
