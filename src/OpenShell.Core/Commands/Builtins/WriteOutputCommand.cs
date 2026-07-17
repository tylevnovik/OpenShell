using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Output</c> command. Per ADR-0048 §2.1.
/// <para>
/// Writes each input object to the success stream. In a pipeline, this is largely a no-op
/// (objects already flow downstream), but explicit <c>Write-Output $x</c> syntax in scripts
/// uses this command. The input objects are wrapped as <see cref="IItem"/> instances and yielded.
/// </para>
/// <para>
/// For multi-value input from the CLI parser, comma-separated tokens are accepted
/// (<c>Write-Output a,b,c</c> yields three items); programmatic callers pass an array directly.
/// </para>
/// </summary>
[Verb("Write", Noun = "Output", Aliases = ["write", "echo"])]
[Description("Writes objects to the success stream.")]
public sealed class WriteOutputCommand : ICommand<WriteOutputCommand.Args>
{
    /// <summary>Arguments for <c>Write-Output</c>.</summary>
    /// <param name="InputObject">Objects to write. Each becomes one <see cref="IItem"/>. Comma-separated tokens are accepted from the CLI parser.</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? InputObject = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (args.InputObject is null || args.InputObject.Length == 0) yield break;
        foreach (var value in args.InputObject)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToItem(value);
        }
    }

    private static IItem ToItem(string? value)
    {
        var display = value ?? string.Empty;
        return new Item
        {
            Path = new Paths.ItemPath { Provider = "fs", InternalPath = display },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", value),
        };
    }
}
