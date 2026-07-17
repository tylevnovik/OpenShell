using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Measure-Command</c> command. Per ADR-0048 §4.5 / §9.1.
/// <para>
/// Executes a script block and returns the wall-clock execution time as a <see cref="TimeSpan"/>.
/// Success-stream output of the block is suppressed.
/// </para>
/// </summary>
[Verb("Measure", Noun = "Command", Aliases = ["measure"])]
[Description("Measures the execution time of a script block.")]
public sealed class MeasureCommandCommand : ICommand<MeasureCommandCommand.Args>
{
    /// <summary>Arguments for <c>Measure-Command</c>.</summary>
    /// <param name="Expression">Script block to measure. Mandatory. Position 0.</param>
    public record Args(
        [property: Parameter(Position = 0)] ScriptBlock? Expression = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.Expression is null)
            throw new ArgumentException("Measure-Command requires -Expression script block.");

        var sw = Stopwatch.StartNew();
        // Synchronous invoke; output is discarded per ADR-0048 §9.1
        _ = args.Expression.Invoke(args.Expression.CapturedContext);
        sw.Stop();

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Measure-Command" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("TotalSeconds", sw.Elapsed.TotalSeconds)
                .With("TotalMilliseconds", sw.Elapsed.TotalMilliseconds)
                .With("Ticks", sw.ElapsedTicks)
                .With("Days", sw.Elapsed.Days)
                .With("Hours", sw.Elapsed.Hours)
                .With("Minutes", sw.Elapsed.Minutes)
                .With("Seconds", sw.Elapsed.Seconds)
                .With("Milliseconds", sw.Elapsed.Milliseconds),
        };
    }
}
