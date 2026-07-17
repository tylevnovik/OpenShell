using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Export-Alias</c> command. Per ADR-0024 §7. Persists user-defined
/// aliases (session, user-global, project) to a TOML file. Built-in aliases derived
/// from <c>[Verb(Aliases)]</c> attributes are never exported.
/// </summary>
[Verb("Export", Noun = "Alias", Aliases = ["epal"])]
[Description("Exports user-defined aliases (no builtins) to a TOML file.")]
public sealed class ExportAliasCommand : ICommand<ExportAliasCommand.Args>
{
    /// <summary>Arguments for <c>Export-Alias</c>.</summary>
    /// <param name="Path">Destination file. Defaults to <c>~/.openshell/aliases.toml</c>.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Path = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        var userDefined = aliases.ListUserDefined();
        var destination = string.IsNullOrWhiteSpace(args.Path)
            ? System.IO.Path.Combine(AliasRegistry.DefaultUserGlobalDir(), "aliases.toml")
            : args.Path!;

        AliasConfigLoader.SaveAliases(destination, userDefined);

        await ctx.Host.WriteOutputLineAsync(
            $"Exported {userDefined.Count} user-defined alias(es) to '{destination}'.", ct);

        yield break;
    }
}
