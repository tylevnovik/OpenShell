using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Completion.Sources;

/// <summary>
/// Completes file and directory paths under the current location. Per ADR-0009.
/// Acts as the fallback source when the token is not a command name, parameter, or variable.
/// Uses the registered provider for the resolved base path so provider-namespaced paths
/// (such as zip and reg prefixes) complete against the correct container.
/// </summary>
public sealed class PathCompletionSource : ICompletionSource
{
    private readonly IProviderRegistry _providers;
    private readonly Func<ItemPath> _currentLocation;

    public PathCompletionSource(IProviderRegistry providers, Func<ItemPath> currentLocation)
    {
        _providers = providers;
        _currentLocation = currentLocation;
    }

    /// <inheritdoc />
    public IReadOnlyList<CompletionItem> GetCompletions(CompletionContext context)
    {
        var parsed = CompletionParser.Parse(context);
        if (parsed.AtStart)
        {
            return Array.Empty<CompletionItem>();
        }

        var token = parsed.Token;
        if (token.StartsWith("-", StringComparison.Ordinal)
            || token.StartsWith("$", StringComparison.Ordinal))
        {
            return Array.Empty<CompletionItem>();
        }

        try
        {
            var location = _currentLocation();
            ItemPath basePath;
            string prefix;
            var lastSeparator = token.LastIndexOfAny(['/', '\\', ':']);
            if (lastSeparator >= 0)
            {
                var parsedPath = ItemPath.Parse(token);
                basePath = location.Combine(parsedPath.InternalPath).GetParent();
                prefix = token[(lastSeparator + 1)..];
            }
            else
            {
                basePath = location;
                prefix = token;
            }

            var container = _providers.ResolveCapability<IContainerProvider>(basePath);
            if (container is null)
            {
                return Array.Empty<CompletionItem>();
            }

            var options = new EnumerationOptions { Recurse = false };
            var results = new List<CompletionItem>();
            foreach (var item in container.GetChildrenAsync(basePath, options).ToBlockingEnumerable())
            {
                if (!item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isDirectory = item.Kind == ItemKind.Directory;
                var text = isDirectory ? item.Name + "/" : item.Name;
                results.Add(new CompletionItem(
                    text,
                    text,
                    isDirectory ? "Directory" : null,
                    CompletionKind.Path));
            }

            return results;
        }
        catch
        {
            return Array.Empty<CompletionItem>();
        }
    }
}
