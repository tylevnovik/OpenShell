using System.Reflection;

namespace OpenShell.Commands;

/// <summary>Static metadata describing a registered command.</summary>
public sealed record CommandDescriptor
{
    public required string Verb { get; init; }
    public required string Noun { get; init; }
    public required string FullName { get; init; }            // "get-childitem"
    public required Type CommandType { get; init; }          // typeof(GetChildItemCommand)
    public required Type ArgsType { get; init; }              // typeof(GetChildItemCommand.Args)
    public string? Description { get; init; }
    public IReadOnlyList<ParameterDescriptor> Parameters { get; init; } = [];
    public string[] Aliases { get; init; } = [];

    /// <summary>Whether the command declared <c>[SupportsShouldProcess]</c>. Per ADR-0049.</summary>
    public bool SupportsShouldProcess { get; init; }

    /// <summary>
    /// Static destructive-impact classification read from <c>[SupportsShouldProcess(ConfirmImpact = ...)]</c>.
    /// Defaults to <see cref="ConfirmImpact.Medium"/> when the attribute is present but no value is set.
    /// Per ADR-0049 §5.
    /// </summary>
    public ConfirmImpact ConfirmImpact { get; init; } = ConfirmImpact.Medium;

    public bool PipelineOnly { get; init; }

    public static CommandDescriptor FromType(Type commandType)
    {
        var verbAttr = commandType.GetCustomAttribute<VerbAttribute>()
            ?? throw new InvalidOperationException(
                $"Command '{commandType.FullName}' is missing [Verb] attribute.");

        var argsProp = commandType.GetNestedTypes().FirstOrDefault(t => t.Name == "Args")
            ?? throw new InvalidOperationException(
                $"Command '{commandType.FullName}' must declare a nested record named 'Args'.");

        var parameters = argsProp.GetProperties()
            .Select(p => new ParameterDescriptor
            {
                Name = p.Name!,
                ParameterAttribute = p.GetCustomAttribute<ParameterAttribute>(),
                Type = p.PropertyType,
            })
            .ToList();

        // Lowercase, hyphen-joined. Verb-only commands (e.g. Help) collapse to just the verb.
        var fullName = string.IsNullOrEmpty(verbAttr.Noun)
            ? verbAttr.Verb.ToLowerInvariant()
            : $"{verbAttr.Verb.ToLowerInvariant()}-{verbAttr.Noun.ToLowerInvariant()}";

        // ADR-0049: read [SupportsShouldProcess(ConfirmImpact = ...)] on the command class.
        var shouldProcessAttr = commandType.GetCustomAttribute<SupportsShouldProcessAttribute>();

        return new CommandDescriptor
        {
            Verb = verbAttr.Verb,
            Noun = verbAttr.Noun,
            FullName = fullName,
            CommandType = commandType,
            ArgsType = argsProp,
            Description = commandType.GetCustomAttribute<DescriptionAttribute>()?.Description,
            Parameters = parameters,
            Aliases = verbAttr.Aliases,
            SupportsShouldProcess = shouldProcessAttr is not null,
            ConfirmImpact = shouldProcessAttr?.ConfirmImpact ?? ConfirmImpact.Medium,
            PipelineOnly = verbAttr.PipelineOnly,
        };
    }
}

public sealed record ParameterDescriptor
{
    public required string Name { get; init; }
    public required ParameterAttribute? ParameterAttribute { get; init; }
    public required Type Type { get; init; }
    public int Position => ParameterAttribute?.Position ?? -1;
    public string[] Aliases => ParameterAttribute?.Aliases ?? [];
    public bool Mandatory => ParameterAttribute?.Mandatory ?? false;
}
