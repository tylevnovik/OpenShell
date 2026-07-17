using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Preview;

/// <summary>
/// 文件名搜索服务。Per ADR-0030 §4.
/// 优先用 <see cref="UsnJournalIndexer"/> (Everything 风格磁盘索引, Windows USN Journal + 非 Windows 目录遍历) 实现快速匹配;
/// 索引未就绪或未覆盖时回退到 <see cref="IContainerProvider.GetChildrenAsync"/> 实时枚举。
/// 流式返回结果, 达到 <see cref="SearchOptions.MaxResults"/> 停止。延迟 &lt; 50ms (per ADR-0030 §9)。
/// </summary>
/// <remarks>
/// 实现策略:
/// <list type="bullet">
///   <item>若 <see cref="UsnJournalIndexer"/> 已加载且根路径在已索引卷内, 走索引匹配 (per ADR-0030 §4: Everything 风格)。</item>
///   <item>否则回退到 provider 枚举 + 简单子串 / fzf 模糊匹配 (per ADR-0030 §4: 简单匹配为默认)。</item>
/// </list>
/// </remarks>
public sealed class FileNameSearchService : IFileNameSearchService
{
    private readonly IProviderRegistry _providers;
    private readonly UsnJournalIndexer? _indexer;

    /// <summary>
    /// 构造搜索服务, 不使用磁盘索引 (M3 兼容)。
    /// </summary>
    public FileNameSearchService(IProviderRegistry providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _indexer = null;
    }

    /// <summary>
    /// 构造搜索服务, 注入 USN 磁盘索引器 (per ADR-0030 §4)。
    /// </summary>
    /// <param name="providers">Provider 注册表。</param>
    /// <param name="indexer">已加载的磁盘索引器 (可为 null, 退回到实时枚举)。</param>
    public FileNameSearchService(IProviderRegistry providers, UsnJournalIndexer? indexer)
        : this(providers)
    {
        _indexer = indexer;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> SearchAsync(
        ItemPath root,
        string query,
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query))
            yield break;

        // 优先用磁盘索引 (per ADR-0030 §4: Everything 风格, 延迟 < 10ms)。
        if (_indexer is not null && TryGetIndexedRoot(root, out var indexedRootPath))
        {
            var emitted = 0;
            foreach (var (key, file) in _indexer.Files)
            {
                if (emitted >= options.MaxResults)
                    yield break;

                ct.ThrowIfCancellationRequested();

                // 仅返回该 root 下的条目。
                if (!key.StartsWith(indexedRootPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (options.FuzzyMatch)
                {
                    if (!FuzzyMatch(file.Name, query, out var score))
                        continue;
                    yield return ToItem(file, score);
                }
                else
                {
                    if (file.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    yield return ToItem(file, 1.0);
                }

                emitted++;
            }
            yield break;
        }

        // 回退路径: provider 实时枚举 + 子串 / 模糊匹配 (per ADR-0030 §4: 简单匹配)。
        var container = _providers.ResolveCapability<IContainerProvider>(root);
        if (container is null)
            yield break;

        var enumOpts = new EnumerationOptions
        {
            Recurse = options.Recurse,
            IncludeHidden = options.IncludeHidden,
            IncludeSystem = options.IncludeHidden,
        };

        var emittedFallback = 0;
        await foreach (var item in container.GetChildrenAsync(root, enumOpts, ct).ConfigureAwait(false))
        {
            if (emittedFallback >= options.MaxResults)
                yield break;

            ct.ThrowIfCancellationRequested();

            // 只索引文件和目录, 跳过属性等非文件项。
            if (item.Kind != ItemKind.File && item.Kind != ItemKind.Directory)
                continue;

            var name = item.Name;
            double score;
            bool matched;
            if (options.FuzzyMatch)
            {
                matched = FuzzyMatch(name, query, out score);
            }
            else
            {
                matched = name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                score = matched ? 1.0 : 0.0;
            }

            if (!matched)
                continue;

            emittedFallback++;
            yield return new SearchResultItem(item, score: score);
        }
    }

    /// <summary>
    /// 将 <see cref="UsnJournalIndexer.IndexedFile"/> 转换为 <see cref="IItem"/>。
    /// 因索引层不持有 provider, 这里构造一个 <see cref="Item"/> (path = fs::&lt;abs&gt;)。
    /// </summary>
    private static IItem ToItem(UsnJournalIndexer.IndexedFile file, double score)
    {
        var itemPath = new ItemPath
        {
            Provider = "fs",
            InternalPath = file.Path.Replace('\\', '/'),
        };
        var item = new Item
        {
            Path = itemPath,
            Kind = ItemKind.File,
            Size = file.Size,
            Timestamps = new ItemTimestamps(null, new DateTimeOffset(file.Modified, TimeSpan.Zero), null),
        };
        return new SearchResultItem(item, score: score);
    }

    /// <summary>
    /// 判断 root 是否在已索引卷内, 返回该卷在索引 key 前缀中使用的根路径 (小写)。
    /// 仅 fs:: / 绝对路径支持索引匹配; 其他 provider / 相对路径返回 false。
    /// </summary>
    private static bool TryGetIndexedRoot(ItemPath root, out string indexedRootPath)
    {
        indexedRootPath = "";
        if (root.Provider != "fs") return false;
        if (!root.IsRooted) return false;
        indexedRootPath = root.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar).ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// fzf 风格子序列模糊匹配: query 字符按顺序出现在 name 中即匹配。
    /// 评分 = 匹配字符数 / name 长度 (匹配越紧凑、占比越高分越高)。
    /// </summary>
    private static bool FuzzyMatch(string name, string query, out double score)
    {
        score = 0;
        if (query.Length == 0)
        {
            score = 1;
            return true;
        }
        if (query.Length > name.Length)
            return false;

        var nameLen = name.Length;
        var qi = 0;
        for (int ni = 0; ni < nameLen && qi < query.Length; ni++)
        {
            if (char.ToLowerInvariant(name[ni]) == char.ToLowerInvariant(query[qi]))
                qi++;
        }

        if (qi < query.Length)
            return false;

        // 匹配的字符数等于 query 长度, 评分按 query 占 name 的比例。
        score = (double)query.Length / nameLen;
        return true;
    }
}
