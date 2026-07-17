using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Function</c> command. Per ADR-0024 §8. Lists all user-defined
/// functions (session, user-global, project). An optional <c>-Name</c> wildcard
/// pattern filters the list.
/// </summary>
[Verb("Get", Noun = "Function", Aliases = ["gfn"])]
[Description("Lists user-defined functions, optionally filtered by -Name pattern.")]
public sealed class GetFunctionCommand : ICommand<GetFunctionCommand.Args>
{
    /// <summary>Arguments for <c>Get-Function</c>.</summary>
    /// <param name="Name">Optional wildcard pattern (e.g. <c>find-*</c>).</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-n" })] string? Name = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        var all = aliases.ListFunctions();
        var filtered = string.IsNullOrEmpty(args.Name)
            ? all
            : all.Where(f => MatchesPattern(f.Name, args.Name!)).ToList();

        await ctx.Host.WriteOutputLineAsync(
            "Name".PadRight(20) + "Parameters".PadRight(25) + "Source".PadRight(12) + "Description", ct);

        foreach (var f in filtered)
        {
            var parameters = f.Parameters.Count == 0
                ? "-"
                : string.Join(", ", f.Parameters);
            await ctx.Host.WriteOutputLineAsync(
                f.Name.PadRight(20)
                + parameters.PadRight(25)
                + SourceLabel(f.Source).PadRight(12)
                + (f.Description ?? "-"), ct);
        }

        yield break;
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase);
    }

    private static string SourceLabel(AliasSource source) => source switch
    {
        AliasSource.Builtin => "builtin",
        AliasSource.UserGlobal => "user",
        AliasSource.Project => "project",
        AliasSource.Session => "session",
        _ => source.ToString().ToLowerInvariant(),
    };
}
