using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Variables;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in Set-Variable command. Per ADR-0042. Sets a user variable.
/// </summary>
[Verb("Set", Noun = "Variable", Aliases = ["set", "sv"])]
[Description("Sets a user variable in the specified scope.")]
[Help(
    Synopsis = "Sets a user variable in the specified scope.",
    Examples = new[]
    {
        "set-variable myvar \"hello\"              # session scope",
        "set-variable threshold 10 -Scope Session  # explicit scope",
    },
    RelatedLinks = new[] { "get-variable", "remove-variable" })]
public sealed class SetVariableCommand : ICommand<SetVariableCommand.Args>
{
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter(Position = 1)] string? Value = null,
        [property: Parameter] VariableScope Scope = VariableScope.Session);

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var vars = ctx.Variables;
        if (vars is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Variable registry is not available in this context.",
                Operation = "set-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        try
        {
            vars.Set(args.Name, args.Value ?? "", args.Scope);
            await ctx.Host.WriteOutputLineAsync(
                $"Set ${args.Name} = '{args.Value}'", ct).ConfigureAwait(false);
        }
        catch (ReadOnlyVariableException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = ex.Message,
                Operation = "set-variable",
                Phase = ErrorPhase.ArgumentBinding,
            });
        }

        yield break;
    }
}
