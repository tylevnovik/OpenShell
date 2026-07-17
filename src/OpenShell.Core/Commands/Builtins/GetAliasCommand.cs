using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Alias</c> command. Per ADR-0024 §7. Lists all currently active
/// aliases (session &gt; user-global &gt; project &gt; builtin) with their resolved source
/// tier. An optional <c>-Name</c> wildcard pattern filters the list (supports
/// <c>*</c> and <c>?</c>).
/// </summary>
[Verb("Get", Noun = "Alias", Aliases = ["gal"])]
[Description("Lists all aliases, optionally filtered by -Name wildcard pattern.")]
public sealed class GetAliasCommand : ICommand<GetAliasCommand.Args>
{
    /// <summary>Arguments for <c>Get-Alias</c>.</summary>
    /// <param name="Name">Optional wildcard pattern (e.g. <c>l*</c>, <c>?s</c>). If omitted, all aliases are listed.</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-n" })] string? Name = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var aliases = ctx.Aliases
            ?? throw new InvalidOperationException("Alias registry is not available in this context.");

        var all = aliases.List();
        var filtered = string.IsNullOrEmpty(args.Name)
            ? all
            : all.Where(a => MatchesPattern(a.Name, args.Name!)).ToList();

        await ctx.Host.WriteOutputLineAsync(
            "Name".PadRight(15) + "Command".PadRight(40) + "Source".PadRight(12) + "Description", ct);

        foreach (var a in filtered)
        {
            await ctx.Host.WriteOutputLineAsync(
                a.Name.PadRight(15)
                + a.Command.PadRight(40)
                + SourceLabel(a.Source).PadRight(12)
                + (a.Description ?? "-"), ct);
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
