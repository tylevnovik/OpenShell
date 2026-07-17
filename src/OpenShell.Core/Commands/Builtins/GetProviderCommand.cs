using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Provider</c> command. Per ADR-0038 §6 / ADR-0039 §5.
/// Lists all registered providers with their version, required API version, stability tier,
/// and declared capabilities. Useful for diagnosing compatibility issues after a host upgrade.
/// </summary>
[Verb("Get", Noun = "Provider", Aliases = ["gpr"])]
[Description("Lists all registered providers with API version and stability metadata.")]
public sealed class GetProviderCommand : ICommand<GetProviderCommand.Args>
{
    /// <summary>Arguments for <c>Get-Provider</c>.</summary>
    public record Args
    {
        /// <summary>Optional provider name filter (case-insensitive). When omitted, all providers are listed.</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }

        /// <summary>When set, re-runs API compatibility checks for every provider and reports mismatches.</summary>
        [Parameter(Aliases = ["check-compat"])]
        public bool CheckCompatibility { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var info in ctx.Providers.Registered)
        {
            if (!string.IsNullOrEmpty(args.Name) &&
                !string.Equals(info.Name, args.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ctx.Providers.TryGet(info.Name, out var provider) || provider is null)
                continue;

            var caps = string.Join(",", provider.Capabilities.Select(c => c.ToString()));
            var compatible = true;
            var compatNote = string.Empty;

            if (args.CheckCompatibility)
            {
                try
                {
                    ApiCompatibilityChecker.Verify(info);
                    compatNote = "OK";
                }
                catch (ApiMismatchException ex)
                {
                    compatible = false;
                    compatNote = ex.Remediation;
                }
            }

            yield return new Item
            {
                Path = new ItemPath { Provider = info.Name, InternalPath = "/" },
                Kind = ItemKind.Unknown,
                Properties = PropertyBag.Empty
                    .With("Name", info.Name)
                    .With("Version", info.Version.ToString())
                    .With("RequiredApiVersion", info.RequiredApiVersion.ToString())
                    .With("HostApiVersion", ProviderApiVersion.Current.ToString())
                    .With("Stability", info.ApiStability.ToString())
                    .With("Capabilities", caps)
                    .With("Compatible", compatible)
                    .With("CompatibilityNote", compatNote)
                    .With("Description", info.Description ?? string.Empty)
                    .With("Author", info.Author ?? string.Empty),
            };

            await Task.Yield();
        }
    }
}
