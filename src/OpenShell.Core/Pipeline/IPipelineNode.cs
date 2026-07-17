using OpenShell.Items;

namespace OpenShell.Pipeline;

/// <summary>
/// Pipeline 节点接口契约。Per ADR-0010.
/// 节点必须实现 IPipelineSource / IPipelineTransform / IPipelineSink 之一，不能多实现。
/// </summary>

/// <summary>
/// Pipeline 起点：产生 IItem 流。Per ADR-0010 §1.
/// 实现者同时实现 <see cref="Commands.ICommand{TArgs}"/>，<c>ExecuteAsync</c> 返回的流即 <c>Produce</c> 的结果。
/// 例如 <c>Get-ChildItem</c>、<c>Get-Item</c>、<c>Get-PSDrive</c>。
/// </summary>
public interface IPipelineSource : Commands.ICommand;

/// <summary>
/// Pipeline 中间变换节点：消费上游流，产出变换后的流。Per ADR-0010 §1.
/// 例如 <c>Where-Object</c>、<c>Sort-Object</c>、<c>Select-Object</c>、<c>Take-Object</c>、<c>Skip-Object</c>。
/// </summary>
public interface IPipelineTransform<TArgs> : Commands.ICommand<TArgs> where TArgs : notnull
{
    /// <summary>变换上游流。必须流式 + 透传 CancellationToken。</summary>
    IAsyncEnumerable<IItem> Transform(IAsyncEnumerable<IItem> input, TArgs args, Commands.CommandContext ctx, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pipeline 终点：消费流，不再产出。Per ADR-0010 §1.
/// 例如 <c>Format-Table</c>、<c>Out-File</c>、<c>Out-Null</c>。
/// </summary>
public interface IPipelineSink<TArgs> : Commands.ICommand<TArgs> where TArgs : notnull
{
    /// <summary>消费上游流。返回 ValueTask 表示完成。</summary>
    ValueTask Consume(IAsyncEnumerable<IItem> input, TArgs args, Commands.CommandContext ctx, CancellationToken cancellationToken = default);
}
