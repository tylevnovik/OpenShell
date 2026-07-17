using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Set-Function</c> command. Per ADR-0024 §8. Defines a session-scoped
/// user function with named parameters and a body (which may contain pipes and
/// multiple statements separated by <c>;</c>). The function is also persisted to
/// <c>~/.openshell/functions.toml</c> so it survives across sessions.
/// Per ADR-0024 §10, the body must not contain <c>exit</c> or <c>return</c> tokens.
/// </summary>
[Verb("Set", Noun = "Function", Aliases = ["sfn"])]
[Description("Defines a session function and persists it to functions.toml.")]
public sealed class SetFunctionCommand : ICommand<SetFunctionCommand.Args>
{
    /// <summary>Arguments for <c>Set-Function</c>.</summary>
    /// <param name="Name">Function name. Cannot contain <c>-</c> or start with a digit (per ADR-0024 §10).</param>
    /// <param name="Body">Function body. May contain pipes and semicolons. Must not contain <c>exit</c> or <c>return</c>.</param>
    /// <param name="Parameters">Optional comma-separated parameter names (e.g. <c>"path,sizeMB"</c>).</param>
    /// <param name="Description">Optional human-readable description.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter(Position = 1, Mandatory = true)] string Body,
        [property: Parameter(Aliases = new[] { "-p" })] string? Parameters = null,
        [property: Parameter(Aliases = new[] { "-d" })] string? Description = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("Function name is required.", nameof(args.Name));
        if (string.IsNullOrWhiteSpace(args.Body))
            throw new ArgumentException("Function body is required.", nameof(args.Body));

        var parameters = ParseParameters(args.Parameters);

        var function = new UserFunction
        {
            Name = args.Name,
            Body = args.Body,
            Parameters = parameters,
            Description = args.Description,
            Source = AliasSource.Session,
        };

        // SetSessionFunction validates the name and body (no '-', no leading digit,
        // no 'exit'/'return' tokens) and runs cycle detection.
        aliases.SetSessionFunction(function);

        // Persist to ~/.openshell/functions.toml so the function survives reload.
        var destination = System.IO.Path.Combine(
            AliasRegistry.DefaultUserGlobalDir(), "functions.toml");
        var existing = AliasConfigLoader.LoadFunctions(destination);
        var merged = new List<UserFunction>();
        foreach (var f in existing)
        {
            if (!string.Equals(f.Name, args.Name, StringComparison.OrdinalIgnoreCase))
            {
                merged.Add(f);
            }
        }
        merged.Add(function);
        AliasConfigLoader.SaveFunctions(destination, merged);

        await ctx.Host.WriteOutputLineAsync(
            $"Defined function '{args.Name}' with {parameters.Count} parameter(s) and persisted to '{destination}'.", ct);

        yield break;
    }

    private static IReadOnlyList<string> ParseParameters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.ToList();
    }
}
