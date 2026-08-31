using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Preview;

/// <summary>
/// ADR-0030 预览与搜索运行时 DI 注册扩展。
/// 在 <c>Program.cs</c> 的 <c>ConfigureServices</c> 中调用 <see cref="AddPreviewRuntime"/> 一次,
/// 注册全部 previewer / 索引器 / 缓存 / 搜索服务。
/// </summary>
/// <remarks>
/// 注册内容 (per ADR-0030):
/// <list type="bullet">
///   <item>6 个 <see cref="IPreviewer"/> (Image / Video / Archive / Pdf / Code / Text, 按优先级顺序注册)。</item>
///   <item><see cref="IPreviewService"/> → <see cref="PreviewService"/> (聚合所有 previewer)。</item>
///   <item><see cref="LruPreviewCache"/> (singleton, LRU 1000, per ADR-0030 §3)。</item>
///   <item><see cref="UsnJournalIndexer"/> (singleton, USN Journal 磁盘索引, per ADR-0030 §4)。</item>
///   <item><see cref="FileIndexStore"/> (singleton, SQLite + FTS5 长期索引, per ADR-0030 §8)。</item>
///   <item><see cref="IFileNameSearchService"/> → <see cref="FileNameSearchService"/> (per ADR-0030 §4)。</item>
/// </list>
/// 约束: <see cref="OpenShell.Commands.Builtins.IQuickLookWindow"/> 的 GUI 实现由 GUI host 自行注册 (Core 不引用 Avalonia)。
/// </remarks>
public static class PreviewServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0030 预览与搜索运行时全部服务。
    /// 幂等目录创建: 调用时确保 <c>~/.openshell/cache/previews/</c> 与 <c>~/.openshell/index/</c> 存在。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>原 <paramref name="services"/> 引用, 便于链式调用。</returns>
    public static IServiceCollection AddPreviewRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 确保 ADR-0030 预览/搜索所需目录存在 (per OpenShellPaths.EnsurePreviewDirs)。
        OpenShellPaths.EnsurePreviewDirs();

        // ---- IPreviewer 注册 (按优先级顺序: 后注册的不影响先注册的 CanPreview 判定) ----
        // PreviewService 按注册顺序遍历, 第一个 CanPreview=true 的 previewer 处理。
        // Code previewer 在 Text 之前: 代码文件优先用语法高亮渲染 (避免 .cs/.py 等落入纯文本)。

        // Image (per ADR-0030 §2: 仅 PNG 原生解码, 其他格式需 SkiaSharp)。
        services.AddSingleton<IPreviewer>(sp =>
            new ImagePreviewer(CreateOpenRead(sp.GetRequiredService<IProviderRegistry>())));

        // Video (per ADR-0030 §2: ffprobe 元数据, 不可用时降级)。
        services.AddSingleton<IPreviewer>(_ =>
            new VideoPreviewer(CreateResolveLocalPath()));

        // Archive (per ADR-0030 §2: zip / tar.gz via BCL)。
        services.AddSingleton<IPreviewer>(sp =>
            new ArchivePreviewer(CreateOpenRead(sp.GetRequiredService<IProviderRegistry>())));

        // PDF (per ADR-0030 §2: 轻量 stream parser, 不依赖 PDFium)。
        services.AddSingleton<IPreviewer>(sp =>
            new PdfPreviewer(CreateOpenRead(sp.GetRequiredService<IProviderRegistry>())));

        // Code (per ADR-0030 §2: ANSI 高亮, 13 种语言)。
        services.AddSingleton<IPreviewer>(sp =>
            new CodePreviewer(CreateOpenRead(sp.GetRequiredService<IProviderRegistry>())));

        // Text (per ADR-0030 §2: 兜底文本预览, 前 1000 行 + 二进制检测)。
        services.AddSingleton<IPreviewer>(sp =>
            new TextPreviewer(CreateOpenRead(sp.GetRequiredService<IProviderRegistry>())));

        // ---- IPreviewService: 聚合所有 IPreviewer (DI 自动收集 IEnumerable<IPreviewer>) ----
        services.AddSingleton<IPreviewService, PreviewService>();

        // ---- LruPreviewCache (per ADR-0030 §3: LRU 1000, SHA256 key, 磁盘持久化) ----
        services.AddSingleton<LruPreviewCache>(sp =>
            new LruPreviewCache(OpenShellPaths.PreviewsCacheDir, sp.GetService<ILogger<LruPreviewCache>>()));

        // ---- UsnJournalIndexer (per ADR-0030 §4: Everything 风格磁盘索引) ----
        services.AddSingleton<UsnJournalIndexer>(sp =>
            new UsnJournalIndexer(OpenShellPaths.FileNameIndexFile, sp.GetService<ILogger<UsnJournalIndexer>>()));

        // ---- FileIndexStore (per ADR-0030 §8: SQLite + FTS5 长期索引) ----
        services.AddSingleton<FileIndexStore>(sp =>
            new FileIndexStore(OpenShellPaths.FileIndexDb, sp.GetService<ILogger<FileIndexStore>>()));

        // 索引库必须在 host 启动时恢复；空索引由 Search-Global 回退到实时枚举。
        services.AddSingleton<FileIndexLifecycleService>();
        services.AddHostedService(sp => sp.GetRequiredService<FileIndexLifecycleService>());

        // ---- IFileNameSearchService (per ADR-0030 §4: 优先索引, 回退 provider 枚举) ----
        services.AddSingleton<IFileNameSearchService>(sp =>
        {
            var providers = sp.GetRequiredService<IProviderRegistry>();
            var indexer = sp.GetService<UsnJournalIndexer>();
            return new FileNameSearchService(providers, indexer);
        });

        return services;
    }

    /// <summary>
    /// 创建 openRead 委托: 通过 <see cref="IContentProvider"/> 打开 ItemPath 对应的内容流。
    /// 被 Text / Image / Pdf / Archive / Code previewer 共用。
    /// </summary>
    private static Func<ItemPath, CancellationToken, Task<Stream>> CreateOpenRead(IProviderRegistry providers)
    {
        return (path, ct) =>
        {
            var content = providers.ResolveCapability<IContentProvider>(path)
                ?? throw new InvalidOperationException(
                    $"Provider '{path.Provider}' does not support content reading (IContentProvider).");
            return content.OpenReadAsync(path, ct).AsTask();
        };
    }

    /// <summary>
    /// 创建 resolveLocalPath 委托: 将 fs ItemPath 转为本地 OS 路径 (供 VideoPreviewer 启动 ffprobe)。
    /// 非 fs provider 返回空字符串 (ffprobe 不可用, 降级为 "metadata unavailable")。
    /// </summary>
    private static Func<ItemPath, string> CreateResolveLocalPath()
    {
        return path => path.Provider == "fs"
            ? path.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar)
            : "";
    }
}
