using OpenShell.Paths;

namespace OpenShell.Sessions;

/// <summary>
/// 会话状态根模型。Per ADR-0034 §1.
/// 一个会话对应一个用户工作区 (例如 "work" / "personal")，独立持久化到 JSON 文件。
/// </summary>
public sealed record Session(
    Guid Id,
    string Name,
    DateTimeOffset Created,
    DateTimeOffset LastActive,
    SessionState State);

/// <summary>
/// 会话状态：当前路径 / 导航历史 / GUI tabs / 活跃 tab。
/// Per ADR-0034 §1, §5. 导航历史最多 100 项 (栈顶为最近访问)。
/// </summary>
public sealed record SessionState(
    ItemPath CurrentLocation,
    IReadOnlyList<ItemPath> NavigationHistory,
    IReadOnlyList<TabState> Tabs,
    int ActiveTabIndex);

/// <summary>
/// GUI tab 状态。Per ADR-0034 §11.
/// CLI host 不使用 tabs (Tabs 为空)；GUI host 持久化每个 tab 的位置与 split-view 配置。
/// </summary>
public sealed record TabState(
    Guid Id,
    string Label,
    PaneState LeftPane,
    PaneState? RightPane,
    bool IsSplitView);

/// <summary>
/// 单个 pane 状态。Per ADR-0034 §11.
/// CustomView / Sort 为 ViewSpec / SortSpec 的名称引用 (简化为 string)，避免序列化完整 ViewSpec 树。
/// </summary>
public sealed record PaneState(
    ItemPath CurrentLocation,
    string? CustomView,
    string? Sort);
