using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Out-GridView</c> command. Per ADR-0048 §2.9.5.
/// <para>
/// GUI host pops a <c>GridViewWindow</c> (ADR-0043). CLI host degrades to
/// <c>Format-Table</c> output and emits a warning.
/// </para>
/// </summary>
[Verb("Out", Noun = "GridView", Aliases = ["ogv"])]
[Description("Displays items in a grid view (GUI) or formatted table (CLI).")]
public sealed class OutGridViewCommand : IPipelineSink<OutGridViewCommand.Args>
{
    /// <summary>Arguments for <c>Out-GridView</c>.</summary>
    public record Args(
        string? Title = null,
        bool PassThru = false,
        string OutputMode = "None");

    /// <summary>
    /// Not supported without pipeline input: <c>Out-GridView</c> is pipeline-only.
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Out-GridView is pipeline-only, use it after |");

    /// <inheritdoc />
    public async ValueTask Consume(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        CancellationToken cancellationToken = default)
    {
        if (ctx.Host.Kind == HostKind.Gui)
        {
            // GUI host would pop a grid view window (ADR-0043).
            // Currently just emit a warning; full GUI integration is future work.
            await ctx.Host.WriteOutputLineAsync(
                $"WARNING: Out-GridView GUI window not yet available. Title: {args.Title ?? "(none)"}",
                cancellationToken).ConfigureAwait(false);
        }

        // CLI degradation: render items as text
        await foreach (var item in input.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var value = item.Properties["Value"] ?? item.Name ?? "";
            await ctx.Host.WriteOutputLineAsync(value?.ToString() ?? "", cancellationToken).ConfigureAwait(false);
        }
    }
}
