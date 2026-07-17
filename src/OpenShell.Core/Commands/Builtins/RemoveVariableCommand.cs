using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Variables;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in Remove-Variable command. Per ADR-0042.
/// </summary>
[Verb("Remove", Noun = "Variable", Aliases = ["rv", "unset"])]
[Description("Removes a user variable.")]
[Help(Synopsis = "Removes a user variable.", RelatedLinks = new[] { "get-variable", "set-variable" })]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Low)]
public sealed class RemoveVariableCommand : ICommand<RemoveVariableCommand.Args>
{
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter] VariableScope Scope = VariableScope.Session);

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var vars = ctx.Variables;
        if (vars is null) yield break;

        // ADR-0049 §7: gate the destructive remove.
        if (!ctx.ShouldProcess($"variable '${args.Name}'", "Remove", ConfirmImpact.Low)) yield break;

        var removed = vars.Remove(args.Name, args.Scope);
        if (!removed)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Variable '${args.Name}' not found or read-only.",
                Operation = "remove-variable",
                Phase = ErrorPhase.Operation,
            });
        }

        yield break;
    }
}
