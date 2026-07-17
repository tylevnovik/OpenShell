using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Clear-Host</c> command. Per ADR-0023 M1. Clears the host output
/// by emitting an ANSI clear-screen escape sequence via
/// <see cref="OpenShell.IHost.WriteOutputLineAsync"/>.
/// </summary>
[Verb("Clear", Noun = "Host", Aliases = ["cls", "clear"])]
[Description("Clears the host output.")]
public sealed class ClearHostCommand : ICommand<ClearHostCommand.Args>
{
    private const string AnsiClearScreen = "\u001b[2J\u001b[H";

    /// <summary>Arguments for <c>Clear-Host</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await ctx.Host.WriteOutputLineAsync(AnsiClearScreen, ct).ConfigureAwait(false);
        yield break;
    }
}
