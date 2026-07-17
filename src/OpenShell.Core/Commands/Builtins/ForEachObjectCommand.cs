using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Runtime;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ForEach-Object</c> command. Per ADR-0048 §1.1.
/// <para>
/// PowerShell's most-used pipeline transform. Per ADR-0046 script blocks are not yet
/// available, so this initial implementation supports two restricted forms:
/// <list type="bullet">
///   <item><c>-MemberName &lt;string&gt;</c> — extract a single property (or standard field)
///     from each upstream item and yield it as a property-bearing <see cref="IItem"/>.
///     Equivalent to <c>ForEach-Object { $_.&lt;MemberName&gt; }</c>.</item>
///   <item><c>-ProcessCommand &lt;string&gt;</c> — invoke a named method on the string
///     representation of each item (e.g. <c>"ToUpper"</c>). Equivalent to
///     <c>ForEach-Object { $_.ToString().&lt;Method&gt;() }</c>.</item>
/// </list>
/// </para>
/// <para>
/// The full script-block form (<c>-Begin</c> / <c>-Process</c> / <c>-End</c>) is future work
/// tracked by ADR-0046.
/// </para>
/// <para>
/// Per ADR-0047 §1.3 (原 M5+ 延迟实现, 现已落实): <c>-Parallel</c> 开关启用并行处理,
/// <c>-ThrottleLimit</c> 控制最大并行度 (默认 5)。并行模式下结果顺序不保证 (与 PowerShell 7 一致)。
/// </para>
/// </summary>
[Verb("ForEach", Noun = "Object", Aliases = ["%", "foreach"], PipelineOnly = true)]
[Description("Performs an operation against each item in a collection.")]
public sealed class ForEachObjectCommand : IPipelineTransform<ForEachObjectCommand.Args>
{
    /// <summary>Arguments for <c>ForEach-Object</c>.</summary>
    /// <param name="ProcessScriptBlock">Script block applied to each upstream item (per ADR-0046). Takes priority over <paramref name="MemberName"/> / <paramref name="ProcessCommand"/>.</param>
    /// <param name="MemberName">Property name to extract per item. Mutually exclusive with <paramref name="ProcessCommand"/>.</param>
    /// <param name="ProcessCommand">Name of a string-returning method to invoke on each item's string representation.</param>
    /// <param name="Parallel">Per ADR-0047 §1.3: 启用并行处理。每个 item 在独立线程中执行 ScriptBlock。</param>
    /// <param name="ThrottleLimit">Per ADR-0047 §1.3: 并行模式下最大并发度。默认 5。</param>
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock? ProcessScriptBlock = null,
        [property: Parameter] string? MemberName = null,
        [property: Parameter] string? ProcessCommand = null,
        [property: Parameter] bool Parallel = false,
        [property: Parameter] int ThrottleLimit = 5);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Per ADR-0047 §1.3: -Parallel 模式。使用 Parallel.ForEachAsync 并行处理, 结果收集到 ConcurrentQueue。
        // 顺序不保证 (与 PowerShell 7 -Parallel 一致)。每个并行任务使用独立 ExecutionContext (基于 CapturedContext)。
        if (args.Parallel && args.ProcessScriptBlock is not null)
        {
            var results = new ConcurrentQueue<IItem>();
            var capturedCtx = args.ProcessScriptBlock.CapturedContext;
            var throttle = Math.Max(1, args.ThrottleLimit);

            // 物化输入为列表 (并行需要多次访问)。
            var items = new List<IItem>();
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
                items.Add(item);

            await Parallel.ForEachAsync(items, new ParallelOptions
            {
                MaxDegreeOfParallelism = throttle,
                CancellationToken = ct,
            }, async (item, token) =>
            {
                // 每个并行任务: 创建单元素流, 调用 InvokeStream 处理。
                // 不依赖 System.Linq.Async 的 ToAsyncEnumerable(), 手动实现单元素异步流。
                await foreach (var outItem in args.ProcessScriptBlock
                    .InvokeStream(SingleItemStream(item), capturedCtx, token)
                    .WithCancellation(token).ConfigureAwait(false))
                {
                    results.Enqueue(outItem);
                }
            }).ConfigureAwait(false);

            foreach (var item in results)
                yield return item;
            yield break;
        }

        // ScriptBlock 形式（per ADR-0046 §5）：脚本块作为 pipeline transform 流式处理每个 item。
        if (args.ProcessScriptBlock is not null)
        {
            await foreach (var outItem in args.ProcessScriptBlock
                .InvokeStream(input, args.ProcessScriptBlock.CapturedContext, ct)
                .WithCancellation(ct).ConfigureAwait(false))
            {
                yield return outItem;
            }
            yield break;
        }

        if (string.IsNullOrEmpty(args.MemberName) && string.IsNullOrEmpty(args.ProcessCommand))
        {
            // No transformation specified: pass items through unchanged (mirrors PowerShell
            // where `... | ForEach-Object` with no -Process emits the input).
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(args.MemberName))
                yield return ProjectMember(item, args.MemberName!);
            else if (!string.IsNullOrEmpty(args.ProcessCommand))
                yield return InvokeStringCommand(item, args.ProcessCommand!);
        }
    }

    /// <summary>
    /// Not supported without pipeline input: <c>ForEach-Object</c> is pipeline-only.
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ForEach-Object is pipeline-only, use it after |");

    private static IItem ProjectMember(IItem item, string memberName)
    {
        var value = ExprEvaluator.GetPropertyValue(memberName, item);
        var display = value?.ToString() ?? string.Empty;
        // Wrap as a Property-kind item with the extracted value as both Name and a Properties entry.
        return new Item
        {
            Path = item.Path,
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With(memberName, value)
                .With("Value", display),
        };
    }

    private static IItem InvokeStringCommand(IItem item, string commandName)
    {
        var input = item.Name;
        var result = commandName switch
        {
            "ToUpper" => input.ToUpperInvariant(),
            "ToLower" => input.ToLowerInvariant(),
            "Trim" => input.Trim(),
            "TrimStart" => input.TrimStart(),
            "TrimEnd" => input.TrimEnd(),
            _ => InvokeReflection(input, commandName),
        };
        return new Item
        {
            Path = item.Path,
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", result),
        };
    }

    private static string InvokeReflection(string input, string methodName)
    {
        var method = typeof(string).GetMethod(
            methodName,
            Type.EmptyTypes);
        if (method is null)
            return input;
        return method.Invoke(input, null) as string ?? input;
    }

    /// <summary>
    /// 创建一个只产出单个 item 的异步流。替代 System.Linq.Async.ToAsyncEnumerable() 以避免额外依赖。
    /// </summary>
    private static async IAsyncEnumerable<IItem> SingleItemStream(
        IItem item,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return item;
    }
}
