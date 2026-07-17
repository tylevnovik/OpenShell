using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.History;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Undo</c> command. Per ADR-0020 §7.
/// 撤销最近一条操作。支持多步撤销: <c>undo 5</c> 撤销最近 5 步 (ADR-0020 §Context)。
/// 反向操作失败时停止后续 undo, 写 ErrorRecord。
/// </summary>
[Verb("Undo", Noun = "", Aliases = ["u"])]
[Description("Undoes the most recent undoable operation.")]
[Help(
    Synopsis = "Undoes the most recent undoable operation.",
    Examples = new[] { "undo", "undo 3" },
    RelatedLinks = new[] { "redo" })]
public sealed class UndoCommand : ICommand<UndoCommand.Args>
{
    /// <summary>Arguments for <c>Undo</c>.</summary>
    /// <param name="Steps">撤销步数, 默认 1。</param>
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
                Operation = "undo",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (!undo.CanUndo)
        {
            await ctx.Host.WriteOutputLineAsync("Nothing to undo.", ct).ConfigureAwait(false);
            yield break;
        }

        var steps = args.Steps <= 0 ? 1 : args.Steps;
        var entry = await undo.UndoAsync(steps, ct).ConfigureAwait(false);
        if (entry is not null)
        {
            // 真实执行反向操作 (delete/move-back/restore-from-trash/rename)。
            // Per ADR-0020 §3, §7.
            await ctx.Host.WriteOutputLineAsync(
                $"Undone: {entry.Operation} (reverse: {entry.Undo?.UndoOperation ?? "(irreversible)"}).", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync("Nothing to undo.", ct).ConfigureAwait(false);
        }

        yield break;
    }
}
