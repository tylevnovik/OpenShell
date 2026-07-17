using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Out-String</c> command. Per ADR-0048 §2.9.4.
/// <para>
/// Renders objects into a string. Default returns a single newline-separated string;
/// <c>-Stream</c> returns an array of strings (one per line).
/// </para>
/// </summary>
[Verb("Out", Noun = "String", Aliases = ["os"], PipelineOnly = true)]
[Description("Renders objects as a string.")]
public sealed class OutStringCommand : IPipelineTransform<OutStringCommand.Args>
{
    /// <summary>Arguments for <c>Out-String</c>.</summary>
    /// <param name="Stream">Return per-line strings instead of one combined string.</param>
    /// <param name="Width">Maximum line width (default 80 or terminal width).</param>
    public record Args(
        bool Stream = false,
        int? Width = null);

    /// <summary>
    /// Not supported without pipeline input: <c>Out-String</c> is pipeline-only.
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Out-String is pipeline-only, use it after |");

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lines = new List<string>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            lines.Add(FormatItem(item));
        }

        if (args.Stream)
        {
            foreach (var line in lines)
            {
                yield return new Item
                {
                    Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Out-String" },
                    Kind = ItemKind.Property,
                    Properties = PropertyBag.Empty.With("Value", line),
                };
            }
        }
        else
        {
            var combined = string.Join(Environment.NewLine, lines);
            yield return new Item
            {
                Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Out-String" },
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty.With("Value", combined),
            };
        }
    }

    private static string FormatItem(IItem item)
    {
        var name = item.Properties["Name"]?.ToString();
        var value = item.Properties["Value"] ?? item.Name;
        return name is not null && value is not null
            ? $"{name}: {value}"
            : value?.ToString() ?? item.Name ?? "";
    }
}
