using OpenShell.Items;

namespace OpenShell.Formatting;

/// <summary>
/// 渲染器接口。Per ADR-0011 §2. 实现者必须无状态、可并发调用、支持 CancellationToken。
/// CLI 实现：TableFormatter / ListFormatter / JsonFormatter / CsvFormatter / MarkdownFormatter。
/// GUI 实现：GridFormatter / OutGridviewFormatter（不是渲染文本，而是构建视图模型）。
/// </summary>
public interface IFormatter
{
    /// <summary>本渲染器支持的视图类型。</summary>
    ViewKind SupportedKind { get; }

    /// <summary>
    /// 流式渲染 items 到 host。返回渲染的行数（不含表头/footer）。
    /// 必须流式 + 透传 CancellationToken；MaxRows 达到后停止消费上游。
    /// </summary>
    ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken cancellationToken = default);
}
