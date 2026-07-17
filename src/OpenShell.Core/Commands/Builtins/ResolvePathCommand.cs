using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Resolve-Path</c> command. Per ADR-0048 §3.2.
/// <para>
/// Resolves relative paths against the host's current location and yields one
/// <see cref="IItem"/> per resolved path. The result's <c>Path</c> carries the absolute
/// <see cref="ItemPath"/>; the <c>ProviderPath</c> / <c>Display</c> properties expose
/// the resolved absolute path string.
/// </para>
/// <para>
/// Wildcard expansion (e.g. <c>C:/Users/*</c>) is not yet implemented; the ADR notes it
/// as future work. Each input path is resolved to a single absolute path.
/// </para>
/// <para>
/// With <c>-Relative</c> the resolved path is rendered relative to the current
/// location (string form).
/// </para>
/// </summary>
[Verb("Resolve", Noun = "Path", Aliases = ["rvpa", "resolve"])]
[Description("Resolves the wildcard characters in a path.")]
public sealed class ResolvePathCommand : ICommand<ResolvePathCommand.Args>
{
    /// <summary>Arguments for <c>Resolve-Path</c>.</summary>
    /// <param name="Path">Paths to resolve. Position 0. Comma-separated tokens accepted from the CLI parser.</param>
    /// <param name="Relative">If <c>true</c>, render the resolved path relative to the current location.</param>
    /// <param name="LiteralPath">Paths to resolve verbatim (no wildcard expansion). Currently handled identically to <see cref="Path"/>.</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Path = null,
        [property: Parameter] bool Relative = false,
        [property: Parameter] string[]? LiteralPath = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;

        var inputs = args.Path ?? args.LiteralPath;
        if (inputs is null || inputs.Length == 0) yield break;

        var current = ctx.Host.CurrentLocation;
        foreach (var raw in inputs)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(raw))
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.InvalidArgument,
                    Message = "Cannot bind an empty string to the Path parameter.",
                    Operation = "resolve-path",
                    Phase = ErrorPhase.ArgumentBinding,
                });
                continue;
            }

            var parsed = ItemPath.Parse(raw);
            var resolved = parsed.IsRooted ? parsed : current.Combine(parsed.InternalPath);

            string display;
            if (args.Relative)
            {
                display = MakeRelative(current, resolved);
            }
            else
            {
                display = resolved.Display;
            }

            yield return new Item
            {
                Path = resolved,
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty
                    .With("ProviderPath", display)
                    .With("Display", display)
                    .With("Provider", resolved.Provider),
            };
        }
    }

    private static string MakeRelative(ItemPath baseLocation, ItemPath target)
    {
        // Same provider: strip the common prefix and return the remainder; fall back to
        // the absolute display when no common prefix exists.
        if (!string.Equals(baseLocation.Provider, target.Provider, StringComparison.Ordinal))
            return target.Display;

        var baseSegs = baseLocation.InternalPath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tgtSegs = target.InternalPath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        int common = 0;
        while (common < baseSegs.Length && common < tgtSegs.Length
               && string.Equals(baseSegs[common], tgtSegs[common], StringComparison.Ordinal))
        {
            common++;
        }

        var up = baseSegs.Length - common;
        var rest = tgtSegs.Length - common;
        if (up == 0 && rest == 0) return ".";
        var parts = new List<string>(up + rest);
        for (int i = 0; i < up; i++) parts.Add("..");
        for (int i = common; i < tgtSegs.Length; i++) parts.Add(tgtSegs[i]);
        return string.Join('/', parts);
    }
}
