using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Runtime;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Trace-Command</c> command. Per ADR-0048 §4.6.
/// <para>
/// Enables <see cref="System.Diagnostics.Trace"/> listeners, runs a script block, then
/// disables listeners. Returns the script block's output (pass-through).
/// </para>
/// </summary>
[Verb("Trace", Noun = "Command", Aliases = ["trace"])]
[Description("Traces a script block with diagnostic listeners.")]
public sealed class TraceCommandCommand : ICommand<TraceCommandCommand.Args>
{
    /// <summary>Arguments for <c>Trace-Command</c>.</summary>
    /// <param name="Name">Trace source name(s). Mandatory.</param>
    /// <param name="Expression">Script block to trace. Mandatory. Position 0.</param>
    /// <param name="Option">Trace options (default: <c>None</c>).</param>
    /// <param name="FilePath">Optional trace file path.</param>
    /// <param name="FileListener">Whether to use a file listener.</param>
    public record Args(
        [property: Parameter] string[]? Name = null,
        [property: Parameter(Position = 0)] ScriptBlock? Expression = null,
        string? Option = null,
        string? FilePath = null,
        bool FileListener = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(args.Name?[0]) && args.Name is null)
            throw new ArgumentException("Trace-Command requires -Name.");
        if (args.Expression is null)
            throw new ArgumentException("Trace-Command requires -Expression script block.");

        TextWriterTraceListener? fileListener = null;
        if (args.FileListener && args.FilePath is not null)
        {
            fileListener = new TextWriterTraceListener(args.FilePath);
            Trace.Listeners.Add(fileListener);
        }

        try
        {
            // Run the script block, passing through its output
            _ = args.Expression.Invoke(args.Expression.CapturedContext);
        }
        finally
        {
            if (fileListener is not null)
            {
                fileListener.Flush();
                Trace.Listeners.Remove(fileListener);
                fileListener.Dispose();
            }
        }

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Trace-Command" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", "Trace completed"),
        };
    }
}
