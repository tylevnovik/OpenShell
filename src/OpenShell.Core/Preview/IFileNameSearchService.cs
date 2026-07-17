using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 文件名搜索服务抽象。Per ADR-0030 §4.
/// 流式返回匹配项, 延迟 &lt; 50ms (per ADR-0030 §9 性能预算)。
/// </summary>
public interface IFileNameSearchService
{
    /// <summary>流式返回匹配项。</summary>
    IAsyncEnumerable<IItem> SearchAsync(ItemPath root, string query, SearchOptions options, CancellationToken ct = default);
}

/// <summary>搜索选项。Per ADR-0030 §4.</summary>
public sealed record SearchOptions(
    bool Recurse = true,
    int MaxResults = 1000,
    bool IncludeHidden = false,
    bool FuzzyMatch = true);
