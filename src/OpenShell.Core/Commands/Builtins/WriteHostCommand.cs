using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Host</c> command. Per ADR-0048 §2.2.
/// <para>
/// Writes a message directly to the host UI (CLI stdout / GUI status panel) without
/// entering the success stream, so downstream pipeline stages cannot capture it.
/// Optional <c>-ForegroundColor</c> / <c>-BackgroundColor</c> accept a
/// <see cref="ConsoleColor"/> name (case-insensitive). <c>-NoNewline</c> suppresses the
/// trailing newline.
/// </para>
/// <para>
/// Color support is best-effort: when the host has no interactive console (output
/// redirected) or an unknown color name is supplied, the message is written without
/// color. This matches PowerShell's graceful degradation under redirection.
/// </para>
/// </summary>
[Verb("Write", Noun = "Host", Aliases = ["wh", "echo"])]
[Description("Writes customized output to a host.")]
public sealed class WriteHostCommand : ICommand<WriteHostCommand.Args>
{
    /// <summary>Arguments for <c>Write-Host</c>.</summary>
    /// <param name="Message">Message text to write. Position 0.</param>
    /// <param name="NoNewline">If <c>true</c>, suppress the trailing newline.</param>
    /// <param name="ForegroundColor">ConsoleColor name for foreground (e.g. <c>Green</c>).</param>
    /// <param name="BackgroundColor">ConsoleColor name for background (e.g. <c>Black</c>).</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Message = null,
        [property: Parameter] bool NoNewline = false,
        [property: Parameter] string? ForegroundColor = null,
        [property: Parameter] string? BackgroundColor = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var message = args.Message ?? string.Empty;
        var fg = ParseColor(args.ForegroundColor);
        var bg = ParseColor(args.BackgroundColor);

        // Apply color only when an interactive console is attached (mirrors PowerShell
        // behaviour under redirection: color codes must not leak into captured output).
        var useColor = !Console.IsOutputRedirected && (fg.HasValue || bg.HasValue);
        if (useColor)
        {
            var prevFg = Console.ForegroundColor;
            var prevBg = Console.BackgroundColor;
            try
            {
                if (fg.HasValue) Console.ForegroundColor = fg.Value;
                if (bg.HasValue) Console.BackgroundColor = bg.Value;
                await WriteAsync(ctx, message, args.NoNewline, ct).ConfigureAwait(false);
            }
            finally
            {
                Console.ForegroundColor = prevFg;
                Console.BackgroundColor = prevBg;
            }
        }
        else
        {
            await WriteAsync(ctx, message, args.NoNewline, ct).ConfigureAwait(false);
        }

        yield break;
    }

    private static async Task WriteAsync(CommandContext ctx, string message, bool noNewline, CancellationToken ct)
    {
        if (noNewline)
        {
            // WriteOutputLineAsync always appends a newline, so use Console.Write for the
            // -NoNewline branch. The host's UI is the console when interactive.
            if (ctx.Host.Kind == HostKind.Cli)
            {
                Console.Write(message);
            }
            else
            {
                // GUI host: emit as a single line via WriteOutputLineAsync (no partial-line API).
                await ctx.Host.WriteOutputLineAsync(message, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync(message, ct).ConfigureAwait(false);
        }
    }

    private static ConsoleColor? ParseColor(string? name)
        => Enum.TryParse<ConsoleColor>(name, ignoreCase: true, out var c) ? c : null;
}
