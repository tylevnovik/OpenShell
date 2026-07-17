using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Command</c> command. Per ADR-0025 §5. Lists all registered
/// commands, aliases, and user functions, optionally filtered by Verb, Noun
/// (glob), Group, or CommandType.
/// </summary>
[Verb("Get", Noun = "Command", Aliases = ["gcm"])]
[Description("Lists all commands, aliases, and functions.")]
[Help(
    Synopsis = "Lists all commands, aliases, and functions, optionally filtered.",
    Examples = new[]
    {
        "get-command                       # list everything",
        "get-command -Verb Get             # filter by verb",
        "get-command -Noun *Item*          # glob match on noun",
        "get-command -Type Alias           # only aliases",
        "get-command -Type Function        # only user functions",
    },
    RelatedLinks = new[] { "get-help", "get-verb" })]
public sealed class GetCommandCommand : ICommand<GetCommandCommand.Args>
{
    private const int CommandTypeWidth = 14;
    private const int NameWidth = 28;
    private const int SourceWidth = 16;
    private const int GroupWidth = 12;

    /// <summary>Arguments for <c>Get-Command</c>.</summary>
    /// <param name="Verb">Optional verb filter (e.g. <c>Get</c>).</param>
    /// <param name="Noun">Optional noun glob pattern (e.g. <c>*Item*</c>).</param>
    /// <param name="Group">Optional group filter. Currently matches the source assembly name.</param>
    /// <param name="Type">Optional type filter: <c>Alias</c>, <c>Function</c>, or <c>Command</c>.</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-v" })] string? Verb = null,
        [property: Parameter(Aliases = new[] { "-n" })] string? Noun = null,
        [property: Parameter(Aliases = new[] { "-g" })] string? Group = null,
        [property: Parameter(Aliases = new[] { "-t" })] string? Type = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typeFilter = NormalizeType(args.Type);
        var verbFilter = args.Verb;
        var nounPattern = args.Noun;
        var groupFilter = args.Group;

        await ctx.Host.WriteOutputLineAsync(
            "CommandType".PadRight(CommandTypeWidth)
            + "Name".PadRight(NameWidth)
            + "Source".PadRight(SourceWidth)
            + "Group", ct);

        var anyEmitted = false;

        // Built-in commands.
        if (typeFilter is null or CommandTypeFilter.Command)
        {
            foreach (var desc in ctx.Commands.Registered.OrderBy(d => d.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (!MatchesVerb(desc.Verb, verbFilter)) continue;
                if (!MatchesNoun(desc.Noun, nounPattern)) continue;
                var (source, group) = SourceAndGroup(desc.CommandType);
                if (!MatchesGroup(group, groupFilter)) continue;

                await ctx.Host.WriteOutputLineAsync(
                    "Command".PadRight(CommandTypeWidth)
                    + desc.FullName.PadRight(NameWidth)
                    + source.PadRight(SourceWidth)
                    + group, ct);
                anyEmitted = true;
            }
        }

        // User aliases (only if alias registry is available).
        if (typeFilter is null or CommandTypeFilter.Alias)
        {
            var aliases = ctx.Aliases?.List();
            if (aliases is not null)
            {
                foreach (var a in aliases)
                {
                    await ctx.Host.WriteOutputLineAsync(
                        "Alias".PadRight(CommandTypeWidth)
                        + a.Name.PadRight(NameWidth)
                        + SourceLabel(a.Source).PadRight(SourceWidth)
                        + "-", ct);
                    anyEmitted = true;
                }
            }
        }

        // User functions.
        if (typeFilter is null or CommandTypeFilter.Function)
        {
            var functions = ctx.Aliases?.ListFunctions();
            if (functions is not null)
            {
                foreach (var f in functions)
                {
                    await ctx.Host.WriteOutputLineAsync(
                        "Function".PadRight(CommandTypeWidth)
                        + f.Name.PadRight(NameWidth)
                        + SourceLabel(f.Source).PadRight(SourceWidth)
                        + "-", ct);
                    anyEmitted = true;
                }
            }
        }

        if (!anyEmitted)
        {
            await ctx.Host.WriteOutputLineAsync("(no commands matched the filter)", ct);
        }

        yield break;
    }

    private static CommandTypeFilter? NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return type.Trim().ToLowerInvariant() switch
        {
            "alias" => CommandTypeFilter.Alias,
            "function" => CommandTypeFilter.Function,
            "command" or "cmd" => CommandTypeFilter.Command,
            _ => null,
        };
    }

    private static bool MatchesVerb(string verb, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return string.Equals(verb, filter!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesNoun(string noun, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        var regexPattern = "^" + Regex.Escape(pattern!)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(noun, regexPattern, RegexOptions.IgnoreCase);
    }

    private static bool MatchesGroup(string group, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return string.Equals(group, filter!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static (string Source, string Group) SourceAndGroup(Type commandType)
    {
        var asm = commandType.Assembly.GetName().Name ?? "Builtins";
        var simple = asm.Replace("OpenShell.", string.Empty);
        return (simple, "Core");
    }

    private static string SourceLabel(AliasSource source) => source switch
    {
        AliasSource.Builtin => "builtin",
        AliasSource.UserGlobal => "user",
        AliasSource.Project => "project",
        AliasSource.Session => "session",
        _ => source.ToString().ToLowerInvariant(),
    };

    private enum CommandTypeFilter { Command, Alias, Function }
}
