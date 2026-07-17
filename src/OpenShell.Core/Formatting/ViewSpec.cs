namespace OpenShell.Formatting;

/// <summary>
/// 视图类型枚举。Per ADR-0011 §1. 决定渲染器选择与 GUI 视图模型映射。
/// </summary>
public enum ViewKind
{
    Table,
    List,
    Json,
    Csv,
    Markdown,
}

/// <summary>
/// 列对齐方式。Per ADR-0011 §1.
/// </summary>
public enum Alignment
{
    Left,
    Right,
    Center,
}

/// <summary>
/// 列规范。Per ADR-0011 §1. 不可变 record，描述一列的属性名、表头、宽度、对齐、格式化字符串。
/// </summary>
public sealed record ColumnSpec
{
    /// <summary>属性名（IItem 标准字段或 Properties 字典 key）。</summary>
    public required string Name { get; init; }

    /// <summary>显示标签（列头），默认与 Name 相同。</summary>
    public string? DisplayLabel { get; init; }

    /// <summary>列宽（CLI 字符数）。null 表示自适应。</summary>
    public int? Width { get; init; }

    /// <summary>对齐方式。默认 Left。</summary>
    public Alignment Align { get; init; } = Alignment.Left;

    /// <summary>格式化字符串，如 "N0" / "yyyy-MM-dd HH:mm"。null 用默认。</summary>
    public string? Format { get; init; }

    /// <summary>是否换行（超长内容折行显示）。M2 默认不实现，保留接口。</summary>
    public bool Wrap { get; init; } = false;
}

/// <summary>
/// 视图规范。Per ADR-0011 §1. 描述一个表格/列表/JSON/CSV 的列定义与渲染选项，CLI/GUI 共享。
/// 不可变 record；formatter 实例必须无状态，可并发调用同一 ViewSpec。
/// </summary>
public sealed record ViewSpec
{
    /// <summary>列定义。空列表表示由 formatter 自动发现（取 Item 标准字段 + Properties.Keys）。</summary>
    public required IReadOnlyList<ColumnSpec> Columns { get; init; }

    /// <summary>视图类型。默认 Table。</summary>
    public ViewKind Kind { get; init; } = ViewKind.Table;

    /// <summary>最大行数。null 表示无限制；达到上限后停止消费上游并提示 truncated。</summary>
    public int? MaxRows { get; init; }

    /// <summary>是否显示表头（Table/CSV 有效）。默认 true。</summary>
    public bool ShowHeader { get; init; } = true;

    /// <summary>是否显示 footer（总数提示）。默认 true。</summary>
    public bool ShowFooter { get; init; } = true;
}
