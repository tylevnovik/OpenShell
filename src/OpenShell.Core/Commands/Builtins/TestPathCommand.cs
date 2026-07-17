using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Test-Path</c> command. Per ADR-0048 §3.1.
/// <para>
/// Determines whether a path exists. With <c>-PathType</c> the check is narrowed to
/// <c>Container</c> (directory-like) or <c>Leaf</c> (file-like). <c>-IsValid</c> only
/// validates path syntax without touching the file system.
/// </para>
/// <para>
/// Yields one <see cref="IItem"/> of kind <see cref="ItemKind.Property"/> per input
/// path, with a <c>Value</c> property holding the boolean result. This matches
/// PowerShell's boolean return value while staying consistent with OpenShell's
/// pipeline model.
/// </para>
/// </summary>
[Verb("Test", Noun = "Path", Aliases = ["tpath", "t"])]
[Description("Determines whether all elements of a path exist.")]
public sealed class TestPathCommand : ICommand<TestPathCommand.Args>
{
    /// <summary>Arguments for <c>Test-Path</c>.</summary>
    /// <param name="Path">Paths to test. Position 0. Comma-separated tokens are accepted from the CLI parser.</param>
    /// <param name="PathType">Narrow the check to <c>Any</c> (default), <c>Container</c>, or <c>Leaf</c>.</param>
    /// <param name="IsValid">If <c>true</c>, only validate path syntax without touching the file system.</param>
    /// <param name="LiteralPath">Paths to test verbatim (no wildcard expansion). Currently handled identically to <see cref="Path"/>; kept for future wildcard support.</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Path = null,
        [property: Parameter] TestPathType PathType = TestPathType.Any,
        [property: Parameter] bool IsValid = false,
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

            if (args.IsValid)
            {
                yield return Result(raw, IsValidPath(raw));
                continue;
            }

            var path = ResolveItemPath(raw, ctx);
            var exists = await PathExistsAsync(path, args.PathType, ctx, ct).ConfigureAwait(false);
            yield return Result(raw, exists);
        }
    }

    private static IItem Result(string path, bool value)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = path },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Path", path)
                .With("Value", value),
        };

    private static bool IsValidPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            _ = ItemPath.Parse(raw);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ItemPath ResolveItemPath(string raw, CommandContext ctx)
    {
        var path = ItemPath.Parse(raw);
        // 非 fs provider 的路径：不与 fs CurrentLocation 组合（跨 provider 路径不互通）。
        if (path.Provider != "fs" || path.IsRooted)
            return path;
        // fs 相对路径：在 fs CurrentLocation 下组合。
        return ctx.CurrentLocation.Provider == "fs"
            ? ctx.CurrentLocation.Combine(path.InternalPath)
            : new ItemPath { Provider = "fs", InternalPath = path.InternalPath };
    }

    private static async ValueTask<bool> PathExistsAsync(
        ItemPath path, TestPathType pathType, CommandContext ctx, CancellationToken ct)
    {
        var itemProvider = ctx.Providers.ResolveCapability<IItemProvider>(path);
        if (itemProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support item retrieval.",
                TargetPath = path,
                Operation = "test-path",
                Phase = ErrorPhase.ProviderResolution,
            });
            return false;
        }

        var item = await itemProvider.GetItemAsync(path, ct).ConfigureAwait(false);
        if (item is null) return false;

        return pathType switch
        {
            TestPathType.Any => true,
            TestPathType.Container => item.Kind == ItemKind.Directory
                || item.Kind == ItemKind.Container,
            TestPathType.Leaf => item.Kind == ItemKind.File
                || item.Kind == ItemKind.SymbolicLink
                || item.Kind == ItemKind.HardLink
                || item.Kind == ItemKind.Property,
            _ => true,
        };
    }
}

/// <summary>
/// Selects the kind of element that <see cref="TestPathCommand"/> must match.
/// Mirrors PowerShell's <c>TestPathType</c> enum (<c>Any</c>, <c>Container</c>, <c>Leaf</c>).
/// </summary>
public enum TestPathType
{
    /// <summary>Match any existing element (file or directory).</summary>
    Any = 0,
    /// <summary>Match only container elements (directories).</summary>
    Container,
    /// <summary>Match only leaf elements (files).</summary>
    Leaf,
}
