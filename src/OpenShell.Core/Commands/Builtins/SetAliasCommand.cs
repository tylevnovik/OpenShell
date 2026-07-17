using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Set-Alias</c> command. Per ADR-0024 §7. Defines a session-scoped
/// alias that overrides any user-global, project, or builtin alias of the same name.
/// The alias is not persisted to disk; use <c>Export-Alias</c> for persistence.
/// </summary>
[Verb("Set", Noun = "Alias", Aliases = ["sal"])]
[Description("Sets a session-scoped alias that overrides lower-priority tiers.")]
public sealed class SetAliasCommand : ICommand<SetAliasCommand.Args>
{
    /// <summary>Arguments for <c>Set-Alias</c>.</summary>
    /// <param name="Name">Alias name. Cannot contain <c>-</c> or start with a digit (per ADR-0024 §10).</param>
    /// <param name="Command">Expansion text. May contain pipes and arguments.</param>
    /// <param name="Description">Optional human-readable description.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter(Position = 1, Mandatory = true)] string Command,
        [property: Parameter(Aliases = new[] { "-d" })] string? Description = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("Alias name is required.", nameof(args.Name));
        if (string.IsNullOrWhiteSpace(args.Command))
            throw new ArgumentException("Alias command is required.", nameof(args.Command));

        aliases.SetSessionAlias(args.Name, args.Command, args.Description);

        await ctx.Host.WriteOutputLineAsync(
            $"Set session alias '{args.Name}' -> '{args.Command}'.", ct);

        yield break;
    }
}
