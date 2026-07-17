using System.Runtime.CompilerServices;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Show-Command</c> command. Per ADR-0048 §9.8.
/// <para>
/// GUI host pops a <c>CommandWindow</c> (per ADR-0043) for interactive parameter
/// entry; CLI host degrades to a <c>Get-Help</c>-style parameter listing and
/// emits a warning. With <c>-PassThru</c>, the command returns the user-filled
/// parameter hashtable instead of executing; in CLI host this always returns an
/// empty hashtable (no interactive form) and prints a warning.
/// </para>
/// </summary>
[Verb("Show", Noun = "Command", Aliases = ["showcmd"])]
[Description("Displays a command parameter form (GUI) or parameter listing (CLI).")]
public sealed class ShowCommandCommand : ICommand<ShowCommandCommand.Args>
{
    /// <summary>Arguments for <c>Show-Command</c>.</summary>
    /// <param name="Name">Command name (mandatory). Position 0.</param>
    /// <param name="PassThru">Return the user-filled parameter hashtable instead of executing the command.</param>
    /// <param name="ErrorPopup">Display errors in a popup window (GUI host only).</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        bool PassThru = false,
        bool ErrorPopup = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var name = args.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            await ctx.Host.WriteOutputLineAsync("show-command: command name is required.", ct)
                .ConfigureAwait(false);
            yield break;
        }

        // GUI host branch: would pop a CommandWindow (ADR-0043).
        if (ctx.Host.Kind == HostKind.Gui)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"WARNING: Show-Command GUI window for '{name}' is not yet available.", ct)
                .ConfigureAwait(false);
            if (args.PassThru)
            {
                // PassThru without an interactive form: yield an empty parameter object.
                yield return ParameterItem(name, new Dictionary<string, object?>());
            }
            yield break;
        }

        // CLI degradation: emit warning + list parameters if help is available.
        await ctx.Host.WriteOutputLineAsync(
            $"WARNING: Show-Command is degraded to a parameter listing in CLI host.", ct)
            .ConfigureAwait(false);

        var help = ctx.Help?.Resolve(name);
        if (help is null)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"show-command: no help found for '{name}'.", ct).ConfigureAwait(false);
            if (args.PassThru)
            {
                yield return ParameterItem(name, new Dictionary<string, object?>());
            }
            yield break;
        }

        // Render parameter listing (similar to Get-Help -Parameter).
        await ctx.Host.WriteOutputLineAsync($"NAME    {help.Name}", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(help.Synopsis))
        {
            await ctx.Host.WriteOutputLineAsync($"SYNOPSIS  {help.Synopsis}", ct).ConfigureAwait(false);
        }
        await ctx.Host.WriteOutputLineAsync("PARAMETERS", ct).ConfigureAwait(false);

        if (help.Parameters.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("  (no parameters documented)", ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var p in help.Parameters)
            {
                var mandatory = p.Mandatory ? "Required" : "Optional";
                var position = p.Position >= 0 ? $"Position {p.Position}" : "Named";
                var aliases = p.Aliases.Count > 0
                    ? $"  Aliases: {string.Join(", ", p.Aliases)}"
                    : "";
                await ctx.Host.WriteOutputLineAsync(
                    $"  -{p.Name} <{p.Type}>  [{mandatory}, {position}]{aliases}", ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(p.Description))
                {
                    await ctx.Host.WriteOutputLineAsync($"      {p.Description}", ct)
                        .ConfigureAwait(false);
                }
            }
        }

        if (args.PassThru)
        {
            // PassThru in CLI host: cannot interactively collect user input; return an empty
            // parameter object so downstream `Get-ChildItem @params` is a no-op.
            await ctx.Host.WriteOutputLineAsync(
                "WARNING: -PassThru is not supported in CLI host; returning empty parameter set.", ct)
                .ConfigureAwait(false);
            yield return ParameterItem(name, new Dictionary<string, object?>());
        }
    }

    private static IItem ParameterItem(string commandName, IDictionary<string, object?> parameters)
    {
        // Wrap as a Property-kind item carrying the (empty) parameter hashtable for splatting.
        var bag = PropertyBag.Empty
            .With("Command", commandName)
            .With("Parameters", parameters);
        return new Item
        {
            Path = ItemPath.Parse($"function::{commandName}"),
            Kind = ItemKind.Property,
            Properties = bag,
        };
    }
}
