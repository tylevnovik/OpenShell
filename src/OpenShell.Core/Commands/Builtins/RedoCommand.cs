using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.History;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Redo</c> command. Per ADR-0020 §8.
/// 重做最近一条被撤销的操作。支持多步重做: <c>redo 3</c> 重做最近 3 步被撤销的操作。
/// 正向操作失败时停止后续 redo, 写 ErrorRecord。
/// </summary>
[Verb("Redo", Noun = "", Aliases = ["r"])]
[Description("Redoes the most recently undone operation.")]
[Help(
    Synopsis = "Redoes the most recently undone operation.",
    Examples = new[] { "redo", "redo 2" },
    RelatedLinks = new[] { "undo" })]
public sealed class RedoCommand : ICommand<RedoCommand.Args>
{
    /// <summary>Arguments for <c>Redo</c>.</summary>
    /// <param name="Steps">重做步数, 默认 1。</param>
    public record Args(
        [property: Parameter(Position = 0)] int Steps = 1);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var undo = ctx.Host.Services.GetService(typeof(IUndoService)) as IUndoService;
        if (undo is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Undo service is not available in this context.",
                Operation = "redo",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (!undo.CanRedo)
        {
            await ctx.Host.WriteOutputLineAsync("Nothing to redo.", ct).ConfigureAwait(false);
            yield break;
        }

        var steps = args.Steps <= 0 ? 1 : args.Steps;
        var entry = await undo.RedoAsync(steps, ct).ConfigureAwait(false);
        if (entry is not null)
        {
            // 真实重新执行正向操作 (copy/move/delete/rename/mkdir/touch)。
            // Per ADR-0020 §8.
            await ctx.Host.WriteOutputLineAsync(
                $"Redone: {entry.Operation}.", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync("Nothing to redo.", ct).ConfigureAwait(false);
        }

        yield break;
    }
}
