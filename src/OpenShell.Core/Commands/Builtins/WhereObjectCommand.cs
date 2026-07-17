using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Runtime;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Where-Object</c> 命令：基于谓词表达式过滤管道项。Per ADR-0010 §1 / ADR-0012.
/// <para>用法示例：<c>ls | where "size &gt; 1MB -and name ~= \"*.txt\""</c></para>
/// <para>谓词在管道内只解析一次，每元素复用 AST 求值。</para>
/// <para>该命令是 pipeline-only：<c>ExecuteAsync</c> 抛 <see cref="NotSupportedException"/>。</para>
/// </summary>
[Verb("Where", Noun = "Object", Aliases = ["where", "filter", "?"])]
[Description("Filters items by a predicate expression.")]
public sealed class WhereObjectCommand : IPipelineTransform<WhereObjectCommand.Args>
{
    /// <summary>参数。</summary>
    /// <param name="FilterScriptBlock">谓词脚本块（per ADR-0046）。每次以 <c>$_</c> 为当前项求值，结果真则保留。优先于 <paramref name="Expression"/>。</param>
    /// <param name="Expression">谓词表达式 DSL 字符串。Per ADR-0012. 作为 ScriptBlock 缺省时的回退形式。</param>
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock? FilterScriptBlock = null,
        [property: Parameter(Position = 0, Mandatory = true)] string Expression = "");

    /// <summary>
    /// 变换上游流：仅保留谓词求值为 true 的项。
    /// 解析错误已通过异常上抛（命令层会包装为 ErrorRecord）。
    /// </summary>
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ScriptBlock 谓词形式（per ADR-0046 §5）：$_ = item，ScriptBlock 返回 bool，true 保留 false 丢弃。
        if (args.FilterScriptBlock is not null)
        {
            var sb = args.FilterScriptBlock;
            var sbCtx = sb.CapturedContext;
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                sbCtx.CurrentItem = item;
                sbCtx.CancellationToken = ct;
                bool keep;
                try
                {
                    var result = sb.Invoke(sbCtx);
                    keep = Evaluator.IsTruthy(result);
                }
                catch
                {
                    // 求值异常默认跳过该元素（per ADR-0012 §7，与字符串谓词语义一致）。
                    keep = false;
                }
                if (keep)
                    yield return item;
            }
            yield break;
        }

        ExprAst ast;
        try
        {
            ast = ExprParser.Parse(args.Expression ?? "");
        }
        catch (FilterParseException ex)
        {
            ctx.Errors?.Write(ex.ToErrorRecord("where-object"));
            yield break;
        }

        var evaluator = new ExprEvaluator();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var ok = false;
            try
            {
                ok = evaluator.Evaluate(ast, item) is true;
            }
            catch
            {
                // 求值异常默认跳过该元素（Per ADR-0012 §7）。
                ok = false;
            }
            if (ok)
                yield return item;
        }
    }

    /// <summary>
    /// 不支持直接调用：<c>Where-Object</c> 是 pipeline-only，必须在管道中使用。
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Where-Object is pipeline-only, use it after |");
}
