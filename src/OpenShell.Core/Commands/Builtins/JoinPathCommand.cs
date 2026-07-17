using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Join-Path</c> command. Per ADR-0048 §3.4.
/// <para>
/// Joins path segments using the OpenShell-canonical <c>/</c> separator. The first
/// segment (<c>-Path</c>) and the second (<c>-ChildPath</c>) are mandatory;
/// <c>-AdditionalChildPath</c> appends further segments (PowerShell 6+ behaviour,
/// OpenShell兼容).
/// </para>
/// <para>
/// Yields a single <see cref="IItem"/> of kind <see cref="ItemKind.Property"/> whose
/// <c>Value</c> is the joined path string. Output uses <c>/</c> as the separator on
/// all platforms (per ADR-0006); callers requiring OS-native separators should pass
/// the result through <c>Resolve-Path</c> or convert explicitly.
/// </para>
/// </summary>
[Verb("Join", Noun = "Path", Aliases = ["join"])]
[Description("Joins a path and a set of child paths into a single path.")]
public sealed class JoinPathCommand : ICommand<JoinPathCommand.Args>
{
    /// <summary>Arguments for <c>Join-Path</c>.</summary>
    /// <param name="Path">The left-hand path segment. Mandatory. Position 0.</param>
    /// <param name="ChildPath">The right-hand path segment. Mandatory. Position 1.</param>
    /// <param name="AdditionalChildPath">Additional segments to append after <paramref name="ChildPath"/>. Comma-separated tokens accepted from the CLI parser.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Path = null,
        [property: Parameter(Position = 1)] string? ChildPath = null,
        [property: Parameter] string[]? AdditionalChildPath = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;

        if (string.IsNullOrEmpty(args.Path))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "The -Path parameter is required.",
                Operation = "join-path",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        if (string.IsNullOrEmpty(args.ChildPath))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "The -ChildPath parameter is required.",
                Operation = "join-path",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var joined = Join(args.Path!, args.ChildPath!);
        if (args.AdditionalChildPath is { } extra)
        {
            foreach (var seg in extra)
            {
                if (seg is { Length: > 0 })
                    joined = Join(joined, seg);
            }
        }

        yield return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = joined },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Path", args.Path!)
                .With("Value", joined),
        };
    }

    /// <summary>
    /// Join two segments with a single <c>/</c> separator. Backslash separators in the
    /// inputs are normalised to <c>/</c> (per ADR-0006). An absolute child path
    /// replaces the parent (mirrors <see cref="ItemPath.Combine"/> semantics).
    /// </summary>
    private static string Join(string left, string right)
    {
        var l = left.Replace('\\', '/').TrimEnd('/');
        var r = right.Replace('\\', '/').TrimStart('/');

        if (string.IsNullOrEmpty(r)) return l;
        // Absolute child (starts with / or a Windows drive letter) replaces the parent —
        // matches PowerShell Join-Path behaviour and ItemPath.Combine semantics.
        if (r[0] == '/' || (r.Length >= 2 && char.IsLetter(r[0]) && r[1] == ':'))
            return r;

        return string.IsNullOrEmpty(l) ? r : $"{l}/{r}";
    }
}
