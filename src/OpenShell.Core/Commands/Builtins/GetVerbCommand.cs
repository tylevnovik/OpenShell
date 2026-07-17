using System.Runtime.CompilerServices;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Verb</c> command. Per ADR-0025 §6. Lists every value of the
/// <see cref="Verb"/> enum with its group and a short human description. Used
/// by command authors to pick a sanctioned verb and by tab-completion engines.
/// </summary>
[Verb("Get", Noun = "Verb", Aliases = ["gv"])]
[Description("Lists all sanctioned verbs with descriptions.")]
[Help(
    Synopsis = "Lists all sanctioned verbs with their group and description.",
    Examples = new[]
    {
        "get-verb                  # list every verb",
        "get-verb | where Group -eq Common",
    },
    RelatedLinks = new[] { "get-command", "get-help" })]
public sealed class GetVerbCommand : ICommand<GetVerbCommand.Args>
{
    private const int VerbWidth = 12;
    private const int GroupWidth = 12;

    /// <summary>Arguments for <c>Get-Verb</c>. Currently takes no parameters; kept for forward compatibility.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await ctx.Host.WriteOutputLineAsync(
            "Verb".PadRight(VerbWidth) + "Group".PadRight(GroupWidth) + "Description", ct);

        foreach (var entry in VerbDescriptions)
        {
            await ctx.Host.WriteOutputLineAsync(
                entry.Verb.ToString().PadRight(VerbWidth)
                + entry.Group.PadRight(GroupWidth)
                + entry.Description, ct);
        }

        yield break;
    }

    private static readonly (Verb Verb, string Group, string Description)[] VerbDescriptions =
    {
        (Verb.Get, "Common", "Retrieve resources"),
        (Verb.Set, "Common", "Modify existing resources"),
        (Verb.New, "Common", "Create new resources"),
        (Verb.Remove, "Common", "Delete resources"),
        (Verb.Move, "Common", "Move resources between locations"),
        (Verb.Copy, "Common", "Copy resources to a new location"),
        (Verb.Rename, "Common", "Rename a resource"),
        (Verb.Invoke, "Action", "Invoke an action or run a script"),
        (Verb.Select, "Pipeline", "Project pipeline objects onto properties"),
        (Verb.Where, "Pipeline", "Filter pipeline objects by predicate"),
        (Verb.Sort, "Pipeline", "Reorder pipeline objects"),
        (Verb.Format, "Output", "Format pipeline output for display"),
        (Verb.Out, "Output", "Direct output to a destination"),
        (Verb.Help, "Meta", "Show help for commands or topics"),
        (Verb.Exit, "Session", "Exit the shell"),
        (Verb.Clear, "Session", "Clear the host output"),
        (Verb.Push, "Stack", "Push current location onto the stack"),
        (Verb.Pop, "Stack", "Pop a location from the stack"),
        // ADR-0039 §5: Provider 包生态命令动词。
        (Verb.Find, "Discovery", "Search for packages in registries"),
        (Verb.Install, "Lifecycle", "Install a package from a registry"),
        (Verb.Update, "Lifecycle", "Upgrade an installed package"),
        (Verb.Uninstall, "Lifecycle", "Remove an installed package"),
        (Verb.Register, "Config", "Add a registry source"),
        (Verb.Unregister, "Config", "Remove a registry source"),
        (Verb.Publish, "Lifecycle", "Publish a package to a registry"),
        // ADR-0048 Tier 1: Critical cmdlets.
        (Verb.ForEach, "Pipeline", "Iterate over each pipeline object"),
        (Verb.Write, "Output", "Write to a stream (output, error, warning, verbose, host)"),
        (Verb.Test, "Lifecycle", "Test a path or condition"),
        (Verb.Resolve, "Navigation", "Resolve a path to its absolute form"),
        (Verb.Split, "Navigation", "Split a path into parts"),
        (Verb.Join, "Navigation", "Join path segments into one path"),
        // ADR-0048 Tier 2: High-priority cmdlets.
        (Verb.ConvertTo, "Data", "Convert objects to a target format (JSON, CSV, HTML, XML)"),
        (Verb.ConvertFrom, "Data", "Convert from a source format (JSON, CSV) to objects"),
        (Verb.Import, "Data", "Import data from a file into objects"),
        (Verb.Export, "Data", "Export objects to a file in a target format"),
        (Verb.Tee, "Pipeline", "Branch pipeline output to a file or variable while passing through"),
        (Verb.Start, "Lifecycle", "Start a process or service"),
        (Verb.Stop, "Lifecycle", "Stop a process or service"),
        (Verb.Wait, "Lifecycle", "Wait for a process to exit"),
    };
}
