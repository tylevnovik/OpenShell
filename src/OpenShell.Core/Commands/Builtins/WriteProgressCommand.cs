using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Progress</c> command. Per ADR-0048 §2.8.
/// <para>
/// Pushes a progress record to the host UI. CLI host shows a single-line progress bar;
/// GUI host shows a dialog (per ADR-0044 <c>ITaskCenter</c>).
/// </para>
/// </summary>
[Verb("Write", Noun = "Progress", Aliases = ["wp"])]
[Description("Displays a progress bar.")]
public sealed class WriteProgressCommand : ICommand<WriteProgressCommand.Args>
{
    /// <summary>Arguments for <c>Write-Progress</c>.</summary>
    /// <param name="Activity">Activity name. Mandatory. Position 0.</param>
    /// <param name="Status">Status text.</param>
    /// <param name="Id">Unique activity ID.</param>
    /// <param name="ParentId">Parent activity ID for nested progress.</param>
    /// <param name="PercentComplete">Completion percentage (0-100).</param>
    /// <param name="SecondsRemaining">Estimated seconds remaining.</param>
    /// <param name="CurrentOperation">Current operation text.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Activity = null,
        string? Status = null,
        int Id = 0,
        int? ParentId = null,
        int? PercentComplete = null,
        int? SecondsRemaining = null,
        string? CurrentOperation = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activity = args.Activity ?? string.Empty;
        var status = args.Status ?? string.Empty;
        var pct = args.PercentComplete ?? -1;

        var line = pct >= 0
            ? $"{activity}: {status} [{pct}%]"
            : $"{activity}: {status}";

        if (!Console.IsOutputRedirected && ctx.Host.Kind == HostKind.Cli)
        {
            const string cyan = "\u001b[36m";
            const string reset = "\u001b[0m";
            await ctx.Host.WriteOutputLineAsync($"{cyan}PROGRESS: {line}{reset}", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync($"PROGRESS: {line}", ct).ConfigureAwait(false);
        }

        yield break;
    }
}
