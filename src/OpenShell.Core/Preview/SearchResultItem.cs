using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 匹配行记录 (用于内容搜索)。Per ADR-0030 §7.
/// line: 1-based 行号; text: 该行文本。
/// </summary>
public sealed record MatchedLine(int Line, string Text);

/// <summary>
/// 搜索结果项。Per ADR-0030 §7.
/// 持有原始 <see cref="IItem"/> + 搜索上下文 (matchedLines / score)。
/// Path / Kind / Name 等委派给原 item, Properties 追加搜索上下文。
/// </summary>
public sealed record SearchResultItem : IItem
{
    public IItem Inner { get; }
    public double Score { get; }
    public IReadOnlyList<MatchedLine> MatchedLines { get; }

    public SearchResultItem(IItem inner, double score = 1.0, IReadOnlyList<MatchedLine>? matchedLines = null)
    {
        Inner = inner;
        Score = score;
        MatchedLines = matchedLines ?? Array.Empty<MatchedLine>();
    }

    /// <inheritdoc />
    public ItemPath Path => Inner.Path;

    /// <inheritdoc />
    public ItemKind Kind => Inner.Kind;

    /// <inheritdoc />
    public ItemTimestamps Timestamps => Inner.Timestamps;

    /// <inheritdoc />
    public long? Size => Inner.Size;

    /// <inheritdoc />
    public string? ContentType => Inner.ContentType;

    /// <inheritdoc />
    public string Name => Inner.Name;

    /// <inheritdoc />
    public PropertyBag Properties => Inner.Properties
        .With("search.score", Score)
        .With("search.matchedLines", MatchedLines);
}
