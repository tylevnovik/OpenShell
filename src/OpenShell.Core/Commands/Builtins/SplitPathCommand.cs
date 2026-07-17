using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Split-Path</c> command. Per ADR-0048 §3.3.
/// <para>
/// Returns a portion of the supplied path(s). Without <c>-Parent</c> / <c>-Leaf</c> /
/// <c>-Qualifier</c> / <c>-NoQualifier</c> the default is <c>-Parent</c> (mirrors
/// PowerShell). Yields one <see cref="IItem"/> per input path with the result in the
/// <c>Value</c> property.
/// </para>
/// </summary>
[Verb("Split", Noun = "Path", Aliases = ["split", "sp"])]
[Description("Returns the specified part of a path.")]
public sealed class SplitPathCommand : ICommand<SplitPathCommand.Args>
{
    /// <summary>Arguments for <c>Split-Path</c>.</summary>
    /// <param name="Path">Paths to split. Position 0. Comma-separated tokens accepted from the CLI parser.</param>
    /// <param name="Parent">Return the parent directory. Mutually exclusive with the other qualifiers.</param>
    /// <param name="Leaf">Return the leaf (file or directory name).</param>
    /// <param name="Qualifier">Return the drive / provider qualifier (e.g. <c>C:</c> or <c>fs::</c>).</param>
    /// <param name="NoQualifier">Return the path with the qualifier stripped.</param>
    /// <param name="IsAbsolute">When <c>true</c>, yield a boolean indicating whether the path is absolute.</param>
    /// <param name="LiteralPath">Paths to split verbatim. Currently handled identically to <see cref="Path"/>.</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Path = null,
        [property: Parameter] bool Parent = false,
        [property: Parameter] bool Leaf = false,
        [property: Parameter] bool Qualifier = false,
        [property: Parameter] bool NoQualifier = false,
        [property: Parameter] bool IsAbsolute = false,
        [property: Parameter] string[]? LiteralPath = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;

        var inputs = args.Path ?? args.LiteralPath;
        if (inputs is null || inputs.Length == 0) yield break;

        foreach (var raw in inputs)
        {
            ct.ThrowIfCancellationRequested();
            yield return Result(raw, Compute(raw, args));
        }
    }

    private static IItem Result(string input, string value)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = input },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Path", input)
                .With("Value", value),
        };

    private static string Compute(string raw, Args args)
    {
        if (args.IsAbsolute)
        {
            // Returned value is a boolean string so callers can introspect; PowerShell
            // returns a bool directly — we expose it via the Value property.
            var parsed = ItemPath.Parse(raw);
            return parsed.IsRooted ? bool.TrueString : bool.FalseString;
        }

        var (qualifier, body) = SplitQualifier(raw);
        var normalized = body.Replace('\\', '/');

        if (args.Qualifier) return qualifier;
        if (args.NoQualifier) return body;
        if (args.Leaf) return LeafOf(normalized);
        // Default and explicit -Parent: parent of the path.
        return ParentOf(normalized);
    }

    private static (string qualifier, string body) SplitQualifier(string raw)
    {
        // Two flavours supported:
        //   1) OpenShell provider-prefixed: "fs::C:/Users/foo" → ("fs::", "C:/Users/foo")
        //   2) Windows drive-qualified: "C:/Users/foo" / "C:\Users\foo" → ("C:", "/Users/foo")
        var idx = raw.IndexOf("::", StringComparison.Ordinal);
        if (idx >= 0)
            return (raw[..(idx + 2)], raw[(idx + 2)..]);

        if (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':')
            return (raw[..2], raw[2..]);

        return (string.Empty, raw);
    }

    private static string ParentOf(string normalized)
    {
        var trimmed = normalized.TrimEnd('/');
        var lastSep = trimmed.LastIndexOf('/');
        if (lastSep < 0) return string.Empty;
        return trimmed[..lastSep];
    }

    private static string LeafOf(string normalized)
    {
        var trimmed = normalized.TrimEnd('/');
        var lastSep = trimmed.LastIndexOf('/');
        return lastSep < 0 ? trimmed : trimmed[(lastSep + 1)..];
    }
}
