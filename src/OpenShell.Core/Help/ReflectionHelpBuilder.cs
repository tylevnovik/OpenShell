using System.Reflection;
using System.Text;
using OpenShell.Commands;

namespace OpenShell.Help;

/// <summary>
/// Builds <see cref="CommandHelp"/> from a <see cref="CommandDescriptor"/> via reflection.
/// Reads <c>[Description]</c> for synopsis, <c>[Help]</c> for examples/related links,
/// and the Args record properties for parameter metadata. Per ADR-0025 §2.
/// </summary>
public static class ReflectionHelpBuilder
{
    /// <summary>
    /// Reflect the command's attributes into a <see cref="CommandHelp"/> record.
    /// Does not consult md files; that is the responsibility of <see cref="IHelpService"/>.
    /// </summary>
    /// <param name="desc">Command descriptor from the registry.</param>
    /// <returns>A baseline <see cref="CommandHelp"/> populated from attributes.</returns>
    public static CommandHelp Build(CommandDescriptor desc)
    {
        ArgumentNullException.ThrowIfNull(desc);

        var helpAttr = desc.CommandType.GetCustomAttribute<HelpAttribute>();
        var classDescription = desc.Description;

        // Synopsis: [Help(Synopsis=...)] takes priority over [Description(...)].
        var synopsis = !string.IsNullOrWhiteSpace(helpAttr?.Synopsis)
            ? helpAttr!.Synopsis
            : classDescription;

        var parameters = BuildParameters(desc.ArgsType);

        var syntax = BuildSyntax(desc.FullName, parameters);

        return new CommandHelp
        {
            Name = desc.FullName,
            Synopsis = synopsis,
            // No long-form description available from reflection alone; md source may override.
            Description = null,
            Syntax = syntax,
            Parameters = parameters,
            Examples = helpAttr?.Examples ?? Array.Empty<string>(),
            RelatedLinks = helpAttr?.RelatedLinks ?? Array.Empty<string>(),
            // OnlineUrl is filled by HelpService if absent here.
            OnlineUrl = helpAttr?.OnlineUrl,
        };
    }

    private static IReadOnlyList<ParameterHelp> BuildParameters(Type argsType)
    {
        var result = new List<ParameterHelp>();
        foreach (var prop in argsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var paramAttr = prop.GetCustomAttribute<ParameterAttribute>();
            if (paramAttr is null) continue;

            var description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description
                              ?? paramAttr.HelpText;

            var help = new ParameterHelp
            {
                Name = prop.Name!,
                Type = SimplifyTypeName(prop.PropertyType),
                Mandatory = paramAttr.Mandatory,
                Position = paramAttr.Position,
                Description = description,
                Aliases = NormalizeAliases(paramAttr.Aliases),
            };
            result.Add(help);
        }

        // Positional parameters first (ascending), then named in declaration order (stable).
        return result
            .OrderBy(p => p.Position >= 0 ? 0 : 1)
            .ThenBy(p => p.Position >= 0 ? p.Position : int.MaxValue)
            .ToList();
    }

    private static string BuildSyntax(string fullName, IReadOnlyList<ParameterHelp> parameters)
    {
        var sb = new StringBuilder(fullName);

        foreach (var p in parameters)
        {
            sb.Append(' ');

            var isSwitch = IsSwitchType(p.Type);
            var isPositional = p.Position >= 0;

            if (isSwitch)
            {
                // [-Recurse]
                sb.Append('[').Append('-').Append(p.Name).Append(']');
            }
            else if (isPositional)
            {
                // Mandatory positional: [-Path] <Type>
                // Optional positional:  [[-Path] <Type>]
                if (p.Mandatory)
                {
                    sb.Append("[-").Append(p.Name).Append("] <").Append(p.Type).Append('>');
                }
                else
                {
                    sb.Append("[[-").Append(p.Name).Append("] <").Append(p.Type).Append(">]");
                }
            }
            else
            {
                // Named: [-Filter <Type>] (or -Filter <Type> if mandatory)
                if (p.Mandatory)
                {
                    sb.Append('-').Append(p.Name).Append(" <").Append(p.Type).Append('>');
                }
                else
                {
                    sb.Append("[-").Append(p.Name).Append(" <").Append(p.Type).Append(">]");
                }
            }
        }

        sb.Append(" [<CommonParameters>]");
        return sb.ToString();
    }

    private static bool IsSwitchType(string simplifiedType)
        => string.Equals(simplifiedType, "SwitchParameter", StringComparison.Ordinal)
           || string.Equals(simplifiedType, "bool", StringComparison.Ordinal);

    /// <summary>
    /// Reduce a CLR type name to a user-friendly display string.
    /// <c>Nullable&lt;T&gt;</c> becomes <c>T</c>, primitives use C# keyword names,
    /// <c>bool</c> is rendered as <c>SwitchParameter</c> in syntax contexts.
    /// </summary>
    /// <param name="type">Property type from the Args record.</param>
    /// <returns>Simplified type name for help display.</returns>
    private static string SimplifyTypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) type = underlying;

        // bool parameters render as SwitchParameter to match the help format
        // in ADR-0025 §3 ([-Recurse [<SwitchParameter>]]).
        if (type == typeof(bool)) return "SwitchParameter";

        var name = type.Name;
        return name switch
        {
            "String" => "string",
            "Int32" => "int",
            "Int64" => "long",
            "Double" => "double",
            "Boolean" => "SwitchParameter",
            _ => name,
        };
    }

    private static IReadOnlyList<string> NormalizeAliases(string[] aliases)
    {
        if (aliases.Length == 0) return Array.Empty<string>();
        var list = new List<string>(aliases.Length);
        foreach (var a in aliases)
        {
            if (string.IsNullOrEmpty(a)) continue;
            // Strip a leading '-' so '-f' becomes 'f' for display.
            list.Add(a.StartsWith('-') ? a[1..] : a);
        }
        return list;
    }
}
