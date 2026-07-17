using System.Text;

namespace OpenShell.Help;

/// <summary>
/// Renders a <see cref="CommandHelp"/> to plain text per ADR-0025 §3.
/// Modes: <see cref="HelpMode.Brief"/> (--help), <see cref="HelpMode.Detailed"/>,
/// <see cref="HelpMode.Full"/>, <see cref="HelpMode.Examples"/>.
/// Output is plain text; ANSI styling is the host's responsibility.
/// </summary>
public static class HelpRenderer
{
    private const string Indent = "    ";
    private const string ParamIndent = "        ";
    private const string ExampleBanner =
        "-------------------------- EXAMPLE {0} --------------------------";

    /// <summary>
    /// Render the given help record using the requested verbosity mode.
    /// </summary>
    /// <param name="help">Help record to render.</param>
    /// <param name="mode">Verbosity mode.</param>
    /// <returns>Plain-text help string, terminated by a newline.</returns>
    public static string Render(CommandHelp help, HelpMode mode)
    {
        ArgumentNullException.ThrowIfNull(help);

        var sb = new StringBuilder();

        if (mode == HelpMode.Examples)
        {
            AppendExamples(sb, help);
            return sb.ToString();
        }

        AppendSection(sb, "NAME", help.Name);
        AppendSection(sb, "SYNOPSIS", help.Synopsis);
        AppendSection(sb, "SYNTAX", help.Syntax);
        AppendParameters(sb, help);

        if (mode is HelpMode.Detailed or HelpMode.Full)
        {
            AppendSection(sb, "DESCRIPTION", help.Description);
        }

        if (mode == HelpMode.Full)
        {
            AppendExamples(sb, help);
            AppendRelatedLinks(sb, help);
        }

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string header, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        sb.Append(header).Append('\n');
        foreach (var line in content.Split('\n'))
        {
            sb.Append(Indent).Append(line.TrimEnd('\r')).Append('\n');
        }
        sb.Append('\n');
    }

    private static void AppendParameters(StringBuilder sb, CommandHelp help)
    {
        if (help.Parameters.Count == 0) return;
        sb.Append("PARAMETERS\n");
        for (var i = 0; i < help.Parameters.Count; i++)
        {
            var p = help.Parameters[i];
            var typeDisplay = IsSwitch(p.Type) ? "[<SwitchParameter>]" : $"<{p.Type}>";
            sb.Append(Indent).Append('-').Append(p.Name).Append(' ').Append(typeDisplay).Append('\n');

            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                foreach (var line in p.Description!.Split('\n'))
                {
                    sb.Append(ParamIndent).Append(line.TrimEnd('\r')).Append('\n');
                }
            }

            if (p.Aliases.Count > 0)
            {
                sb.Append(ParamIndent)
                  .Append("Aliases: ")
                  .Append(string.Join(", ", p.Aliases.Select(a => "-" + a)))
                  .Append('\n');
            }

            if (p.Mandatory)
            {
                sb.Append(ParamIndent).Append("Required? true").Append('\n');
            }
            if (p.Position >= 0)
            {
                sb.Append(ParamIndent).Append("Position? ").Append(p.Position).Append('\n');
            }

            // Blank line between parameters (but not after the last).
            if (i < help.Parameters.Count - 1) sb.Append('\n');
        }
        sb.Append('\n');
    }

    private static void AppendExamples(StringBuilder sb, CommandHelp help)
    {
        if (help.Examples.Count == 0) return;
        sb.Append("EXAMPLES\n");
        for (var i = 0; i < help.Examples.Count; i++)
        {
            sb.Append(Indent).Append(string.Format(ExampleBanner, i + 1)).Append('\n');
            foreach (var line in help.Examples[i].Split('\n'))
            {
                sb.Append(Indent).Append(line.TrimEnd('\r')).Append('\n');
            }
            if (i < help.Examples.Count - 1) sb.Append('\n');
        }
        sb.Append('\n');
    }

    private static void AppendRelatedLinks(StringBuilder sb, CommandHelp help)
    {
        if (help.RelatedLinks.Count == 0 && string.IsNullOrEmpty(help.OnlineUrl)) return;
        sb.Append("RELATED LINKS\n");
        foreach (var link in help.RelatedLinks)
        {
            sb.Append(Indent).Append(link).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(help.OnlineUrl))
        {
            sb.Append(Indent).Append(help.OnlineUrl).Append('\n');
        }
        sb.Append('\n');
    }

    private static bool IsSwitch(string typeDisplay)
        => string.Equals(typeDisplay, "SwitchParameter", StringComparison.Ordinal);
}
