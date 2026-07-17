using System.Collections.Concurrent;
using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// Default <see cref="IProviderRegistry"/> implementation. Thread-safe.
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly ConcurrentDictionary<string, IProvider> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ProviderInfo> Registered => _byName.Values.Select(p => p.Info).ToList();

    public void Register(IProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_byName.TryAdd(provider.Info.Name, provider))
            throw new InvalidOperationException(
                $"Provider '{provider.Info.Name}' is already registered. " +
                "Per ADR-0004, duplicate registrations are fail-fast.");
    }

    public bool Unregister(string providerName)
        => _byName.TryRemove(providerName, out _);

    public IProvider Get(string providerName)
        => TryGet(providerName, out var p) ? p! : throw new ProviderNotFoundException(providerName);

    public bool TryGet(string providerName, out IProvider? provider)
        => _byName.TryGetValue(providerName, out provider);

    public T? Resolve<T>(string providerName) where T : class
        => TryGet(providerName, out var p) ? p as T : null;

    public IProvider ResolveProvider(ItemPath path)
        => Get(path.Provider);

    public T? ResolveCapability<T>(ItemPath path) where T : class
        => TryGet(path.Provider, out var p) ? p as T : null;
}
