using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Verbose</c> command. Per ADR-0048 §2.5.
/// <para>
/// Writes a verbose message to the host UI. Visibility is controlled by
/// <c>$VerbosePreference</c> or the <c>-Verbose</c> common parameter; the variable
/// subsystem is not yet wired up in M4, so by default verbose messages are emitted.
/// </para>
/// <para>
/// Verbose messages do not enter the success stream, so this command yields no items.
/// </para>
/// </summary>
[Verb("Write", Noun = "Verbose", Aliases = ["wv", "verbose"])]
[Description("Writes a message to the verbose stream.")]
public sealed class WriteVerboseCommand : ICommand<WriteVerboseCommand.Args>
{
    /// <summary>Arguments for <c>Write-Verbose</c>.</summary>
    /// <param name="Message">Verbose text. Mandatory. Position 0.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Message = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var message = args.Message ?? string.Empty;
        var line = $"VERBOSE: {message}";

        if (!Console.IsOutputRedirected && ctx.Host.Kind == HostKind.Cli)
        {
            const string cyan = "\u001b[36m";
            const string reset = "\u001b[0m";
            await ctx.Host.WriteOutputLineAsync($"{cyan}{line}{reset}", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync(line, ct).ConfigureAwait(false);
        }

        yield break;
    }
}
