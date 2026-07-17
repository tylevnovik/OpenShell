using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Remove-Function</c> command. Per ADR-0024 §8. Removes a session-scoped
/// user function. Has no effect on user-global or project functions; to remove a
/// user-global function, edit <c>~/.openshell/functions.toml</c> and reload.
/// </summary>
[Verb("Remove", Noun = "Function", Aliases = ["rfn"])]
[Description("Removes a session-scoped user function.")]
public sealed class RemoveFunctionCommand : ICommand<RemoveFunctionCommand.Args>
{
    /// <summary>Arguments for <c>Remove-Function</c>.</summary>
    /// <param name="Name">Name of the session function to remove.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("Function name is required.", nameof(args.Name));

        var removed = aliases.RemoveSessionFunction(args.Name);
        if (removed)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Removed session function '{args.Name}'.", ct);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync(
                $"No session function named '{args.Name}' found.", ct);
        }

        yield break;
    }
}
