using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Remove-Alias</c> command. Per ADR-0024 §7. Removes a session-scoped
/// alias. Has no effect on user-global, project, or builtin aliases; to remove a
/// user-global alias, edit <c>~/.openshell/aliases.toml</c> and reload.
/// </summary>
[Verb("Remove", Noun = "Alias", Aliases = ["ral"])]
[Description("Removes a session-scoped alias.")]
public sealed class RemoveAliasCommand : ICommand<RemoveAliasCommand.Args>
{
    /// <summary>Arguments for <c>Remove-Alias</c>.</summary>
    /// <param name="Name">Name of the session alias to remove.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("Alias name is required.", nameof(args.Name));

        var removed = aliases.RemoveSessionAlias(args.Name);
        if (removed)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Removed session alias '{args.Name}'.", ct);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync(
                $"No session alias named '{args.Name}' found.", ct);
        }

        yield break;
    }
}
