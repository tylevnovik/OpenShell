# ADR-0010: Pipeline 对象管道模型

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M2
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0002 (异步流式), ADR-0003 (不可变 Item), ADR-0004 (命令系统)

## Context

M2 需要支持管道：

```
get-childitem -r | where size > 1MB | sort by size desc | select name,size | format-table
```

需求：

1. **流式**：百万级文件不能一次性装入内存，必须 `IAsyncEnumerable` 链式
2. **结构化**：管道传递的是 `IItem` 对象（含属性），不是文本行
3. **可取消**：Ctrl+C 中断整条管道
4. **可组合**：节点之间无强耦合，新节点（如 `group-by`）易插入
5. **CLI/GUI 共用**：GUI 的列表筛选也走同一 Pipeline，而非自己写 LINQ
6. **错误传播**：某节点出错，整条管道如何处理（短路 / 容错继续）

参考选项：
- PowerShell 对象流：`PSObject` 通过 `Pipeline` 串接，但 PS 的实现耦合 host
- Nushell：强类型 `Value`，Pipeline 是 `PipelineData`，节点是 `Command`，思路类似我们要的
- Unix 管道：字节流，不够结构化
- LINQ：`IEnumerable<T>.Where(...).Select(...)`，同步、不能流式取消

## Decision

采用 **`IAsyncEnumerable<IItem>` 链 + 节点函数**模型，不引入新容器类型：

### 1. Pipeline 节点接口

```csharp
public interface IPipelineSource : ICommand
{
    IAsyncEnumerable<IItem> Produce(CommandContext ctx, CancellationToken ct);
}

public interface IPipelineTransform : ICommand
{
    IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        CommandContext ctx,
        CancellationToken ct);
}

public interface IPipelineSink : ICommand
{
    ValueTask Consume(
        IAsyncEnumerable<IItem> input,
        CommandContext ctx,
        CancellationToken ct);
}
```

- `get-childitem` 是 `IPipelineSource`（产生流）
- `where` / `select` / `sort` / `take` / `skip` / `group-by` 是 `IPipelineTransform`
- `format-table` / `format-list` / `out-gridview` / `out-file` 是 `IPipelineSink`
- 单条命令（如 `copy-item`）不实现这些接口，是普通 `ICommand`

### 2. Parser 编排

Parser 把 `a | b | c | d` 解析为节点列表，调度器链式执行：

```csharp
public sealed class PipelineExecutor
{
    public async ValueTask ExecuteAsync(
        IReadOnlyList<PipelineNode> nodes,
        CommandContext ctx,
        CancellationToken ct)
    {
        if (nodes.Count == 0) return;

        IAsyncEnumerable<IItem> stream = ((IPipelineSource)nodes[0].Command)
            .Produce(ctx, ct);

        for (int i = 1; i < nodes.Count - 1; i++)
        {
            stream = ((IPipelineTransform)nodes[i].Command)
                .Transform(stream, ctx, ct);
        }

        var sink = nodes[^1].Command;
        if (sink is IPipelineSink s)
            await s.Consume(stream, ctx, ct);
        else
            await ctx.Host.WriteItemsAsync(stream, ct);
    }
}
```

若整条管道以 Source+Transform 结尾（无 Sink），默认 Sink 是 `Out-Default`（调 `Host.WriteItemsAsync`）。

### 3. 节点参数解析

每个节点是独立的命令实例，独立解析自己的 Args。Parser 把每段 `\|` 之间的 tokens 传给该节点的 `Args` 解析器，复用 ADR-0004 的反射参数系统。

### 4. 错误传播策略

- 单元素错误（如 `where` 中属性缺失）：默认跳过该元素，发出 warning；`--strict` 模式短路
- 节点级错误（如 Source 失败）：整条管道失败，`OperationCanceledException` 风格抛出
- Sink 错误：上游已被消费的部分不回滚

### 5. 流式取消

`CancellationToken` 透传到每个节点的 `Transform` / `Produce`。任一节点抛 `OperationCanceledException`，整条管道停止，不再消费上游。

### 6. 排序、分组等"需要全量"的节点

某些 transform 必须缓存全部输入才能输出（`sort`、`group-by`、`distinct`）。这些节点：

- 内部用 `IAsyncEnumerable` 全部消费到 `List<IItem>` 后再输出
- 文档明确标注 "buffering" 性质
- 提供 `--top N` 参数提前终止上游（如 `sort by size desc --top 10` 只需维护 top-10 堆，不全量缓存）

### 7. GUI 复用

GUI 的"过滤框"也调用同一 `IPipelineTransform`，把 `ListBox.ItemsSource` 转换为过滤后的流，无需自写 LINQ。M3 实现。

## Alternatives Considered

1. **PowerShell 风格 `PSObject` 流**：被否决，PSObject 自带 host 上下文，跨 host 难
2. **Nushell 风格 `Value` 强类型**：被否决，引入新类型系统，与 `IItem` 双重模型
3. **LINQ 表达式树**：被否决，C# 表达式不能跨进程、不能 DSL 解析
4. **`IObservable<T>` Reactive**：被否决，背压与取消语义复杂，`IAsyncEnumerable` 更直接
5. **每节点一个 `Task` 异步任务**：被否决，无背压，大流会 OOM

## Consequences

### 优势
- 流式 + 零中间缓存（除 sort/group-by）
- 节点独立可测、可组合
- CLI 与 GUI 复用同一组 transform
- Ctrl+C 自然传播
- 新增节点只需实现接口，不改调度器

### 代价
- 错误传播策略需用户理解（默认跳过 vs 严格模式）
- 调试时无法看到中间流（需 `--tee` 调试选项，M3+ 加）
- `sort`/`group-by` 全量缓存的内存压力

### 约束
- Pipeline 节点必须实现 `IPipelineSource/Transform/Sink` 之一，不能多实现
- `IPipelineTransform` 不允许阻塞上游（不能 `ToArray` 后再处理，除非显式 buffering 节点）
- `CancellationToken` 必须透传到最底层 IO
- 节点实例每条管道新建，禁止跨管道共享状态
- 节点描述符必须标注 `PipelineOnly = true`（ADR-0004），不进 GUI 菜单
- 默认 Sink `Out-Default` 必须可被 `--out` 参数覆盖
