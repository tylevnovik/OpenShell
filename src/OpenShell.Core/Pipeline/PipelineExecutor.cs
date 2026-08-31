using System.Reflection;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Runtime;
using ExecutionContext = OpenShell.Runtime.ExecutionContext;

namespace OpenShell.Pipeline;

/// <summary>
/// Pipeline 调度器：把 <c>a | b | c | d</c> 的命令链串接为
/// Source → Transform* → Sink 的 IAsyncEnumerable&lt;IItem&gt; 流式执行。Per ADR-0010 §2.
/// </summary>
public sealed class PipelineExecutor
{
    private readonly ICommandRegistry _commands;

    /// <summary>
    /// 可选 ExecutionContext 工厂。Per ADR-0046 §4.1 闭包语义: ScriptBlock 参数需要 ExecutionContext 用于闭包捕获。
    /// 工厂按调用时上下文构造 (含当前 Variables/Commands/Host 等), null 时退化为空 ExecutionContext (旧行为)。
    /// </summary>
    private readonly Func<ExecutionContext>? _executionContextFactory;

    public PipelineExecutor(ICommandRegistry commands, Func<ExecutionContext>? executionContextFactory = null)
    {
        _commands = commands;
        _executionContextFactory = executionContextFactory;
    }

    /// <summary>
    /// 执行一条管道（可能含多个 | 分段）。
    /// 若 <paramref name="line"/> 不含 <c>|</c>，退化为单命令调度，返回 false 表示未走管道路径。
    /// </summary>
    public async Task<bool> TryExecuteAsync(
        string line,
        Func<CommandContext> ctxFactory,
        Func<CommandContext, IAsyncEnumerable<IItem>, Task> defaultSink,
        CancellationToken ct = default)
    {
        // 按 | 分段（不考虑引号内的 |，M2 简化：假设引号内无管道）。
        var segments = SplitPipeline(line);
        if (segments.Count <= 1)
            return false;

        var nodes = new List<PipelineNode>(segments.Count);
        foreach (var seg in segments)
        {
            var desc = ResolveSegmentCommand(seg);
            if (desc is null)
                throw new InvalidOperationException($"command not found in pipeline: {seg.Trim()}");
            var args = ParseArgs(desc, seg);
            var instance = (ICommand)Activator.CreateInstance(desc.CommandType)!;
            nodes.Add(new PipelineNode(desc, instance, args));
        }

        var ctx = ctxFactory();
        try
        {
            await ExecuteNodesAsync(nodes, ctx, defaultSink, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.Errors?.Write(ErrorRecord.FromException(ex, phase: ErrorPhase.Operation));
            throw;
        }
    }

    private async Task ExecuteNodesAsync(
        IReadOnlyList<PipelineNode> nodes,
        CommandContext ctx,
        Func<CommandContext, IAsyncEnumerable<IItem>, Task> defaultSink,
        CancellationToken ct)
    {
        if (nodes.Count == 0) return;

        // 第一个节点必须是 Source：调 ExecuteAsync 得到流。
        var head = nodes[0];
        var stream = InvokeSource(head, ctx, ct);

        // 中间节点：Transform 链式。
        for (int i = 1; i < nodes.Count - 1; i++)
        {
            stream = InvokeTransform(nodes[i], stream, ctx, ct);
        }

        // 末节点：若是 Sink 调 Consume；否则用默认 Sink（Out-Default = Host.WriteItemsAsync）。
        var tail = nodes[^1];
        if (ReferenceEquals(head, tail))
        {
            // 单节点（不应该进入这里，因为 segments.Count > 1 已过滤；防御性处理）
            await defaultSink(ctx, stream);
            return;
        }

        if (TryInvokeSink(tail, stream, ctx, ct, out var sinkTask))
        {
            await sinkTask!;
            return;
        }

        // 末节点是 Source/Transform 类型（无 Sink 接口），走默认 Sink。
        // 但末节点若是 Transform（如 sort），需要先消费它再走默认 Sink。
        if (tail.Descriptor.CommandType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineTransform<>)))
        {
            stream = InvokeTransform(tail, stream, ctx, ct);
        }
        else if (!typeof(IPipelineSource).IsAssignableFrom(tail.Descriptor.CommandType)
            && tail.Descriptor.CommandType.GetMethod("ExecuteAsync") is not null)
        {
            // 普通命令（非 Source/Transform/Sink）作为末节点：M2 简化，先消费 stream 到默认 Sink。
            // 未来可作为"边命令"处理（如 copy-item 接收上游输入作为参数），M2 不支持。
        }

        await defaultSink(ctx, stream);
    }

    private static IAsyncEnumerable<IItem> InvokeSource(PipelineNode node, CommandContext ctx, CancellationToken ct)
    {
        // Source 命令通过 ExecuteAsync 返回 IAsyncEnumerable<IItem>。
        var method = node.Descriptor.CommandType.GetMethod("ExecuteAsync")!;
        var task = (IAsyncEnumerable<IItem>)method.Invoke(node.Instance, new object?[] { node.Args, ctx, ct })!;
        return task;
    }

    private static IAsyncEnumerable<IItem> InvokeTransform(PipelineNode node, IAsyncEnumerable<IItem> input, CommandContext ctx, CancellationToken ct)
    {
        // 通过反射找到 IPipelineTransform<TArgs>.Transform 方法。
        var transformInterface = node.Descriptor.CommandType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineTransform<>));
        var method = transformInterface.GetMethod("Transform")!;
        return (IAsyncEnumerable<IItem>)method.Invoke(node.Instance, new object?[] { input, node.Args, ctx, ct })!;
    }

    private static bool TryInvokeSink(PipelineNode node, IAsyncEnumerable<IItem> input, CommandContext ctx, CancellationToken ct, out Task? sinkTask)
    {
        var sinkInterface = node.Descriptor.CommandType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineSink<>));
        if (sinkInterface is null)
        {
            sinkTask = null;
            return false;
        }
        var method = sinkInterface.GetMethod("Consume")!;
        var valueTask = (ValueTask)method.Invoke(node.Instance, new object?[] { input, node.Args, ctx, ct })!;
        sinkTask = valueTask.AsTask();
        return true;
    }

    private CommandDescriptor? ResolveSegmentCommand(string segment)
    {
        var tokens = SplitTokens(segment);
        if (tokens.Count == 0) return null;
        return _commands.Resolve(tokens[0]);
    }

    private object ParseArgs(CommandDescriptor desc, string segment)
    {
        var tokens = SplitTokens(segment);
        return CommandArgumentBinder.Bind(desc, tokens.Skip(1).ToArray(), ConvertValue);
    }

    private object? ConvertValue(Type targetType, string value)
    {
        if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is { } underlying)
            return ConvertValue(underlying, value);
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(bool)) return bool.TryParse(value, out var b) ? b : value.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(long)) return long.Parse(value);
        if (targetType == typeof(OpenShell.Paths.ItemPath)) return OpenShell.Paths.ItemPath.Parse(value);
        if (targetType == typeof(string[])) return value.Split(',');
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
        // Per ADR-0046 §5: ScriptBlock 参数只接受 { ... } 形式。
        // 非 { } 字符串返回 null 让 positional binding 落到下一个候选参数（如 Expression DSL 字符串）。
        if (targetType == typeof(ScriptBlock))
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                // { ... } 形式：去掉外层花括号，解析内部为 ScriptBlockAst，包装为 ScriptBlockExpression + ScriptBlock。
                // Per ADR-0046 §4.1 闭包语义: 使用注入的 ExecutionContext 工厂构造, 捕获当前作用域/命令/宿主。
                // 若工厂未注入 (旧路径), 退化为空 ExecutionContext, 闭包变量不可用 (与旧行为一致)。
                try
                {
                    var inner = trimmed[1..^1];
                    var scriptAst = OpenShell.Parsing.PowerShellParser.Parse(inner);
                    var blockExpr = new OpenShell.Parsing.Ast.ScriptBlockExpression(
                        scriptAst.Statements,
                        scriptAst.Parameters,
                        scriptAst.Span);
                    var ctx = _executionContextFactory?.Invoke() ?? new ExecutionContext();
                    return new ScriptBlock(blockExpr, ctx);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }
        return Convert.ChangeType(value, targetType);
    }

    private static List<string> SplitPipeline(string line)
    {
        // M2 简化：不处理引号内的 |。M3 再补完整词法分析。
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQuote = !inQuote;
            if (ch == '|' && !inQuote)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
                sb.Append(ch);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static List<string> SplitTokens(string segment)
    {
        var result = new List<string>();
        var inQuote = false;
        var current = new System.Text.StringBuilder();
        foreach (var ch in segment)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private sealed record PipelineNode(CommandDescriptor Descriptor, ICommand Instance, object Args);
}
