using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Variables;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in Get-Variable command. Per ADR-0042. Lists or queries variables.
/// </summary>
[Verb("Get", Noun = "Variable", Aliases = ["gv", "echo"])]
[Description("Lists variables or queries a specific variable.")]
[Help(
    Synopsis = "Lists variables or queries a specific variable.",
    Examples = new[]
    {
        "get-variable                       # list all variables",
        "get-variable -Name PATH           # query a specific variable",
        "echo $HOME                         # alias form",
    },
    RelatedLinks = new[] { "set-variable", "remove-variable" })]
public sealed class GetVariableCommand : ICommand<GetVariableCommand.Args>
{
    public record Args(
        [property: Parameter(Position = 0)] string? Name = null,
        [property: Parameter] VariableScope? Scope = null);

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
                Operation = "get-variable",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (args.Name is { } name)
        {
            var value = vars.Resolve(name, args.Scope ?? VariableScope.Session);
            await ctx.Host.WriteOutputLineAsync(value?.ToString() ?? "", ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var kv in vars.List(args.Scope))
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"{kv.Key,-30} = {kv.Value}", ct).ConfigureAwait(false);
            }
        }

        yield break;
    }
}
