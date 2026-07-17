using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Warning</c> command. Per ADR-0048 §2.4.
/// <para>
/// Writes a warning message to the host UI. In a CLI host, warnings are rendered with
/// ANSI yellow text (per ADR-0008 §4) and prefixed with <c>WARNING:</c> (mirrors
/// PowerShell). Visibility is controlled by <c>$WarningPreference</c>; the variable
/// subsystem is not yet wired up in M4, so warnings are always emitted.
/// </para>
/// <para>
/// Warnings do not enter the success stream, so this command yields no items.
/// </para>
/// </summary>
[Verb("Write", Noun = "Warning", Aliases = ["ww", "warning"])]
[Description("Writes a warning message.")]
public sealed class WriteWarningCommand : ICommand<WriteWarningCommand.Args>
{
    /// <summary>Arguments for <c>Write-Warning</c>.</summary>
    /// <param name="Message">Warning text. Mandatory. Position 0.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Message = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var message = args.Message ?? string.Empty;
        var line = $"WARNING: {message}";

        if (!Console.IsOutputRedirected && ctx.Host.Kind == HostKind.Cli)
        {
            const string yellow = "\u001b[33m";
            const string reset = "\u001b[0m";
            await ctx.Host.WriteOutputLineAsync($"{yellow}{line}{reset}", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync(line, ct).ConfigureAwait(false);
        }

        yield break;
    }
}
