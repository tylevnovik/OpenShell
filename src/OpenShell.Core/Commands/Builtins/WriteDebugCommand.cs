using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Debug</c> command. Per ADR-0048 §2.6.
/// <para>
/// Writes a debug message to the host UI. In a CLI host, debug messages are rendered with
/// ANSI yellow text and prefixed with <c>DEBUG:</c> (mirrors PowerShell). Visibility is
/// controlled by <c>$DebugPreference</c>; the variable subsystem is not yet wired up, so
/// debug messages are always emitted.
/// </para>
/// <para>
/// Debug messages do not enter the success stream, so this command yields no items.
/// </para>
/// </summary>
[Verb("Write", Noun = "Debug", Aliases = ["wd"])]
[Description("Writes a debug message.")]
public sealed class WriteDebugCommand : ICommand<WriteDebugCommand.Args>
{
    /// <summary>Arguments for <c>Write-Debug</c>.</summary>
    /// <param name="Message">Debug text. Mandatory. Position 0.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Message = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var message = args.Message ?? string.Empty;
        var line = $"DEBUG: {message}";

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
