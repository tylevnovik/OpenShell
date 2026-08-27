using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenShell.Commands;

namespace OpenShell.Help;

/// <summary>
/// Default <see cref="IHelpService"/>. Per ADR-0025.
/// Resolution order: user override (<c>~/.openshell/help/&lt;cmd&gt;.md</c>) &gt;
/// built-in md (<c>docs/commands/&lt;cmd&gt;.md</c>) &gt; reflection from attributes &gt;
/// online (returns <see cref="CommandHelp.OnlineUrl"/> for the host to open).
/// </summary>
public sealed class HelpService : IHelpService
{
    private const string DefaultOnlineBaseUrl = "https://openshell.dev/commands/";

    private static readonly Regex SafeCommandNameRegex = new(
        @"^[a-z0-9-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeTopicNameRegex = new(
        @"^[a-zA-Z0-9_]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] BuiltInTopics =
    {
        "about_providers",
        "about_pipeline",
        "about_aliases",
        "about_functions",
        "about_path_syntax",
        "about_filter_dsl",
        "about_formatting",
        "about_undo",
        "about_remote",
        "about_security",
    };

    private readonly ICommandRegistry _commands;
    private readonly string _docsBaseDir;
    private readonly string _userOverrideDir;

    /// <summary>
    /// Construct a help service.
    /// </summary>
    /// <param name="commands">Command registry for alias resolution and reflection fallback.</param>
    /// <param name="docsBaseDir">Base directory containing <c>docs/commands/</c> and <c>docs/about/</c>. Defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="userOverrideDir">User override directory. Defaults to <c>~/.openshell/help</c>.</param>
    /// <param name="onlineBaseUrl">Online base URL. Defaults to <c>https://openshell.dev/commands/</c>.</param>
    public HelpService(
        ICommandRegistry commands,
        string? docsBaseDir = null,
        string? userOverrideDir = null,
        string? onlineBaseUrl = null)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _docsBaseDir = docsBaseDir ?? AppContext.BaseDirectory;
        _userOverrideDir = userOverrideDir ?? DefaultUserOverrideDir();
        OnlineBaseUrl = onlineBaseUrl ?? DefaultOnlineBaseUrl;
    }

    /// <inheritdoc />
    public string? OnlineBaseUrl { get; }

    /// <inheritdoc />
    public CommandHelp? Resolve(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return null;

        var input = commandName.Trim();
        // Strip a leading '-' so '-Path' style invocations also work for help lookup.
        if (input.StartsWith('-')) input = input[1..];

        // Resolve alias → full command name via the registry.
        var descriptor = _commands.Resolve(input);
        if (descriptor is null) return null;

        var fullName = descriptor.FullName;
        if (!SafeCommandNameRegex.IsMatch(fullName)) return null;

        var baseline = ReflectionHelpBuilder.Build(descriptor);

        // Try user override, then built-in md. Either may partially override the baseline.
        var userPath = Path.Combine(_userOverrideDir, fullName + ".md");
        var userContent = TryReadAllText(userPath);
        if (userContent is not null)
        {
            try
            {
                var parsed = MarkdownHelpParser.Parse(userContent);
                return MergeWithBaseline(baseline, parsed, fullName);
            }
            catch
            {
                // Per ADR-0025 §10: user override parse failure falls back to built-in/reflection.
            }
        }

        var builtinPath = Path.Combine(_docsBaseDir, "docs", "commands", fullName + ".md");
        var builtinContent = TryReadAllText(builtinPath);
        if (builtinContent is not null)
        {
            try
            {
                var parsed = MarkdownHelpParser.Parse(builtinContent);
                return MergeWithBaseline(baseline, parsed, fullName);
            }
            catch
            {
                // Fall through to baseline.
            }
        }

        // No md available; ensure OnlineUrl is set from the default convention.
        return EnsureOnlineUrl(baseline, fullName);
    }

    /// <inheritdoc />
    public string Render(CommandHelp help, HelpMode mode)
        => HelpRenderer.Render(help, mode);

    /// <inheritdoc />
    public IReadOnlyList<string> ListTopics()
    {
        // Built-in topics shipped with the executable.
        var set = new HashSet<string>(BuiltInTopics, StringComparer.Ordinal);

        // Plus any md files physically present under docs/about/.
        var aboutDir = Path.Combine(_docsBaseDir, "docs", "about");
        if (Directory.Exists(aboutDir))
        {
            foreach (var file in Directory.EnumerateFiles(aboutDir, "about_*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                // Strip locale suffix (e.g. about_aliases.zh-CN → about_aliases).
                var dot = name.IndexOf('.');
                if (dot >= 0) name = name[..dot];
                if (SafeTopicNameRegex.IsMatch(name)) set.Add(name);
            }
        }

        return set.OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public string? ResolveTopic(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName)) return null;
        var name = topicName.Trim();
        if (!SafeTopicNameRegex.IsMatch(name)) return null;

        var aboutDir = Path.Combine(_docsBaseDir, "docs", "about");
        if (!Directory.Exists(aboutDir)) return null;

        // Locale-aware lookup: <name>.<culture>.md → <name>.<two-letter>.md → <name>.md
        var culture = CultureInfo.CurrentUICulture;
        var candidates = new List<string>(3);
        if (!string.IsNullOrEmpty(culture.Name))
            candidates.Add($"{name}.{culture.Name}.md");
        if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
            candidates.Add($"{name}.{culture.TwoLetterISOLanguageName}.md");
        candidates.Add($"{name}.md");

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(aboutDir, candidate);
            if (File.Exists(path))
            {
                try { return File.ReadAllText(path); }
                catch { /* ignore IO errors, fall through */ }
            }
        }

        return null;
    }

    private static CommandHelp EnsureOnlineUrl(CommandHelp help, string fullName)
    {
        if (!string.IsNullOrWhiteSpace(help.OnlineUrl)) return help;
        return help with { OnlineUrl = DefaultOnlineBaseUrl + fullName };
    }

    private static CommandHelp MergeWithBaseline(CommandHelp baseline, ParsedMarkdown parsed, string fullName)
    {
        return EnsureOnlineUrl(new CommandHelp
        {
            Name = baseline.Name,
            Synopsis = Coalesce(parsed.Synopsis, baseline.Synopsis),
            Description = Coalesce(parsed.Description, baseline.Description),
            Syntax = Coalesce(parsed.Syntax, baseline.Syntax),
            Parameters = parsed.Parameters.Count > 0 ? parsed.Parameters : baseline.Parameters,
            Examples = parsed.Examples.Count > 0 ? parsed.Examples : baseline.Examples,
            RelatedLinks = parsed.RelatedLinks.Count > 0 ? parsed.RelatedLinks : baseline.RelatedLinks,
            OnlineUrl = Coalesce(parsed.OnlineUrl, baseline.OnlineUrl),
        }, fullName);
    }

    private static string? Coalesce(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string DefaultUserOverrideDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return Path.Combine(home, ".openshell", "help");
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Minimal markdown help parser. Recognises YAML frontmatter (<c>---</c> fences)
    /// for <c>synopsis</c>, <c>onlineUrl</c>, and <c>## SECTION</c> headings for the rest.
    /// </summary>
    private static class MarkdownHelpParser
    {
        public static ParsedMarkdown Parse(string content)
        {
            var result = new ParsedMarkdown();
            if (string.IsNullOrEmpty(content)) return result;

            var body = content;
            // Frontmatter: leading "---\n...\n---"
            if (content.StartsWith("---", StringComparison.Ordinal))
            {
                var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end >= 0)
                {
                    var frontmatter = content[3..end];
                    ParseFrontmatter(frontmatter, result);
                    body = content[(end + 4)..].TrimStart('\r', '\n');
                }
            }

            ParseSections(body, result);
            return result;
        }

        private static void ParseFrontmatter(string frontmatter, ParsedMarkdown result)
        {
            foreach (var rawLine in frontmatter.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (value.Length == 0) continue;
                switch (key.ToLowerInvariant())
                {
                    case "synopsis": result.Synopsis = value; break;
                    case "onlineurl": result.OnlineUrl = value; break;
                    case "description": result.Description = value; break;
                }
            }
        }

        private static void ParseSections(string body, ParsedMarkdown result)
        {
            var sections = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            var currentHeader = string.Empty;
            var current = new StringBuilder();

            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var trimmed = line.AsSpan().Trim();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (currentHeader.Length > 0)
                        sections[currentHeader] = current;

                    currentHeader = trimmed[3..].ToString().Trim();
                    current = new StringBuilder();
                }
                else if (currentHeader.Length > 0)
                {
                    current.AppendLine(line);
                }
            }

            if (currentHeader.Length > 0)
                sections[currentHeader] = current;

            if (sections.TryGetValue("DESCRIPTION", out var desc))
                result.Description = desc.ToString().TrimEnd();
            if (sections.TryGetValue("SYNTAX", out var syn))
                result.Syntax = syn.ToString().TrimEnd();
            if (sections.TryGetValue("SYNOPSIS", out var ss))
                result.Synopsis = ss.ToString().TrimEnd();

            if (sections.TryGetValue("EXAMPLES", out var ex))
            {
                var examples = ex.ToString();
                result.Examples = ParseExamples(examples);
            }

            if (sections.TryGetValue("RELATED LINKS", out var rl))
            {
                var links = rl.ToString()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
                result.RelatedLinks = links;
            }
        }

        private static IReadOnlyList<string> ParseExamples(string examplesBlock)
        {
            // Each example is preceded by a banner of dashes containing "EXAMPLE N".
            var lines = examplesBlock.Split('\n');
            var result = new List<string>();
            var current = new StringBuilder();
            var inExample = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                var trimmed = line.AsSpan().Trim();
                // Banner lines start with at least 4 dashes and contain "EXAMPLE".
                if (trimmed.StartsWith("----", StringComparison.Ordinal)
                    && trimmed.ToString().Contains("EXAMPLE", StringComparison.OrdinalIgnoreCase))
                {
                    if (inExample && current.Length > 0)
                        result.Add(current.ToString().TrimEnd());
                    current.Clear();
                    inExample = true;
                    continue;
                }

                if (inExample)
                    current.AppendLine(line);
            }

            if (inExample && current.Length > 0)
                result.Add(current.ToString().TrimEnd());

            return result;
        }
    }

    private sealed class ParsedMarkdown
    {
        public string? Synopsis { get; set; }
        public string? Description { get; set; }
        public string? Syntax { get; set; }
        public string? OnlineUrl { get; set; }
        public IReadOnlyList<string> Examples { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> RelatedLinks { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ParameterHelp> Parameters { get; set; } = Array.Empty<ParameterHelp>();
    }
}
