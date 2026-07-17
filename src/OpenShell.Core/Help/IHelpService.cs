using OpenShell.Commands;

namespace OpenShell.Help;

/// <summary>
/// Help service. Per ADR-0025.
/// Resolution order: user override > built-in md > attribute summary > online.
/// </summary>
public interface IHelpService
{
    /// <summary>Resolve help content for a command name (or alias).</summary>
    CommandHelp? Resolve(string commandName);

    /// <summary>Render the help text for the given command.</summary>
    /// <param name="help">Help record to render.</param>
    /// <param name="mode">Render verbosity: <c>brief</c> (--help), <c>detailed</c> (-detailed), <c>full</c> (-full), <c>examples</c> (-examples).</param>
    string Render(CommandHelp help, HelpMode mode);

    /// <summary>List all topics starting with <c>about_</c>.</summary>
    IReadOnlyList<string> ListTopics();

    /// <summary>Resolve an about_* topic by name. Returns rendered text.</summary>
    string? ResolveTopic(string topicName);

    /// <summary>Online base URL. Returns null if offline.</summary>
    string? OnlineBaseUrl { get; }
}

public enum HelpMode { Brief, Detailed, Full, Examples }

/// <summary>In-memory representation of a command's help.</summary>
public sealed record CommandHelp
{
    public required string Name { get; init; }
    public string? Synopsis { get; init; }
    public string? Description { get; init; }
    public string? Syntax { get; init; }
    public IReadOnlyList<ParameterHelp> Parameters { get; init; } = Array.Empty<ParameterHelp>();
    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RelatedLinks { get; init; } = Array.Empty<string>();
    public string? OnlineUrl { get; init; }
}

public sealed record ParameterHelp
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Mandatory { get; init; }
    public int Position { get; init; } = -1;
    public string? Description { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>Class-level help attribute. Per ADR-0025.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class HelpAttribute : Attribute
{
    public string? Synopsis { get; init; }
    public string[] Examples { get; init; } = [];
    public string? OnlineUrl { get; init; }
    public string[] RelatedLinks { get; init; } = [];
}
