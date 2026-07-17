using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Start-Sleep</c> command. Per ADR-0048 §9.2.
/// <para>
/// Suspends the shell for the specified duration. Implemented via <see cref="Task.Delay(int, CancellationToken)"/>
/// with <see cref="CancellationToken"/> pass-through so Ctrl+C can interrupt.
/// </para>
/// <para>
/// <c>-Seconds</c> and <c>-Milliseconds</c> are mutually exclusive.
/// </para>
/// </summary>
[Verb("Start", Noun = "Sleep", Aliases = ["sleep"])]
[Description("Suspends the shell for a period of time.")]
public sealed class StartSleepCommand : ICommand<StartSleepCommand.Args>
{
    /// <summary>Arguments for <c>Start-Sleep</c>.</summary>
    /// <param name="Seconds">Seconds to sleep. Mutually exclusive with <c>Milliseconds</c>.</param>
    /// <param name="Milliseconds">Milliseconds to sleep. Mutually exclusive with <c>Seconds</c>.</param>
    public record Args(
        int? Seconds = null,
        int? Milliseconds = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.Seconds is null && args.Milliseconds is null)
            throw new ArgumentException("Start-Sleep requires -Seconds or -Milliseconds.");
        if (args.Seconds is not null && args.Milliseconds is not null)
            throw new ArgumentException("Start-Sleep -Seconds and -Milliseconds are mutually exclusive.");

        var delay = args.Seconds is int s
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromMilliseconds(args.Milliseconds ?? 0);

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);

        yield break;
    }
}
