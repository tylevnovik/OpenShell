using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Help</c> command. Per ADR-0025 §3. Resolves help for a command
/// or <c>about_*</c> topic and renders it to the host output stream. The
/// <c>-Online</c> switch opens the canonical web page in the default browser.
/// </summary>
[Verb("Get", Noun = "Help", Aliases = ["help", "man", "h"])]
[Description("Shows help for a command or about_ topic.")]
[Help(
    Synopsis = "Shows help for a command or about_ topic.",
    Examples = new[]
    {
        "get-help get-childitem                       # brief help",
        "get-help get-childitem -detailed             # with description",
        "get-help get-childitem -full                 # everything",
        "get-help get-childitem -examples            # only examples",
        "get-help get-childitem -online              # open web docs",
        "get-help about_providers                     # about topic",
    },
    RelatedLinks = new[] { "get-command", "get-verb" })]
public sealed class GetHelpCommand : ICommand<GetHelpCommand.Args>
{
    /// <summary>Arguments for <c>Get-Help</c>.</summary>
    /// <param name="Name">Command name, alias, or <c>about_*</c> topic name.</param>
    /// <param name="Detailed">Show DESCRIPTION in addition to brief fields.</param>
    /// <param name="Full">Show all sections including EXAMPLES and RELATED LINKS.</param>
    /// <param name="Examples">Show only the EXAMPLES section.</param>
    /// <param name="Online">Open the online documentation in the default browser.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter(Aliases = new[] { "-d" })] bool Detailed = false,
        [property: Parameter(Aliases = new[] { "-f" })] bool Full = false,
        [property: Parameter(Aliases = new[] { "-e" })] bool Examples = false,
        [property: Parameter(Aliases = new[] { "-o" })] bool Online = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var help = ctx.Help
            ?? throw new InvalidOperationException("Help service is not available in this context.");

        if (string.IsNullOrWhiteSpace(args.Name))
        {
            await ctx.Host.WriteOutputLineAsync("get-help: command name is required.", ct);
            yield break;
        }

        var name = args.Name.Trim();

        // about_* topic branch.
        if (name.StartsWith("about_", StringComparison.OrdinalIgnoreCase))
        {
            var topic = help.ResolveTopic(name);
            if (topic is null)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"get-help: topic '{name}' was not found.", ct);
                yield break;
            }

            await ctx.Host.WriteOutputLineAsync(topic, ct);
            yield break;
        }

        // Command branch.
        var resolved = help.Resolve(name);
        if (resolved is null)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"get-help: no help found for '{name}'.", ct);
            yield break;
        }

        if (args.Online)
        {
            if (string.IsNullOrWhiteSpace(resolved.OnlineUrl))
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"get-help: no online URL is registered for '{resolved.Name}'.", ct);
            }
            else
            {
                OpenInDefaultBrowser(resolved.OnlineUrl!);
                await ctx.Host.WriteOutputLineAsync(
                    $"Opening {resolved.OnlineUrl} in default browser...", ct);
            }

            yield break;
        }

        var mode = SelectMode(args);
        var rendered = help.Render(resolved, mode);
        await ctx.Host.WriteOutputLineAsync(rendered, ct);
        yield break;
    }

    private static HelpMode SelectMode(Args args)
    {
        if (args.Full) return HelpMode.Full;
        if (args.Detailed) return HelpMode.Detailed;
        if (args.Examples) return HelpMode.Examples;
        return HelpMode.Brief;
    }

    private static void OpenInDefaultBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = "cmd";
                psi.Arguments = $"/c start \"\" \"{url}\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi.FileName = "open";
                psi.Arguments = $"\"{url}\"";
                psi.UseShellExecute = false;
            }
            else
            {
                // Assume Linux / other Unix.
                psi.FileName = "xdg-open";
                psi.Arguments = $"\"{url}\"";
                psi.UseShellExecute = false;
            }

            Process.Start(psi);
        }
        catch
        {
            // Browser launch failures are non-fatal; the host already has the URL printed.
        }
    }
}
