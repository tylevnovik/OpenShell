using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Convert-Path</c> command. Per ADR-0048 §3.5.
/// <para>
/// Converts a provider path (with <c>provider::</c> prefix) to the equivalent filesystem path.
/// Non-filesystem providers throw <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
[Verb("Convert", Noun = "Path", Aliases = ["cvpa"])]
[Description("Converts a provider path to a filesystem path.")]
public sealed class ConvertPathCommand : ICommand<ConvertPathCommand.Args>
{
    /// <summary>Arguments for <c>Convert-Path</c>.</summary>
    /// <param name="Path">Path to convert. Mandatory. Position 0. Supports pipeline.</param>
    /// <param name="LiteralPath">Literal path (no wildcard expansion). Alias for Path when wildcards are not desired.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Path = null,
        string? LiteralPath = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var inputPath = args.Path ?? args.LiteralPath;
        if (string.IsNullOrEmpty(inputPath))
            throw new ArgumentException("Convert-Path requires -Path or -LiteralPath.");

        var itemPath = ItemPath.Parse(inputPath);

        // Non-fs providers cannot be converted to filesystem paths
        if (itemPath.Provider != "fs")
            throw new InvalidOperationException(
                $"Cannot convert path in '{itemPath.Provider}' provider to a filesystem path.");

        yield return new Item
        {
            Path = itemPath,
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Path", itemPath.InternalPath)
                .With("Value", itemPath.InternalPath),
        };
    }
}
