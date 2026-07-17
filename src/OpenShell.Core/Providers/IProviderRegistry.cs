using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// Host-side registry for providers. Per ADR-0005, providers can be loaded from external assemblies.
/// Per ADR-0001, capabilities can be queried via the <see cref="Resolve{T}"/> pattern.
/// </summary>
public interface IProviderRegistry
{
    IReadOnlyCollection<ProviderInfo> Registered { get; }

    void Register(IProvider provider);
    bool Unregister(string providerName);

    IProvider Get(string providerName);
    bool TryGet(string providerName, out IProvider? provider);

    /// <summary>Resolve a capability for a provider. Returns null if not supported.</summary>
    T? Resolve<T>(string providerName) where T : class;

    /// <summary>Resolve a provider from a path's provider segment.</summary>
    IProvider ResolveProvider(ItemPath path);

    /// <summary>Resolve a capability from a path. Throws if provider missing, returns null if capability missing.</summary>
    T? ResolveCapability<T>(ItemPath path) where T : class;
}

/// <summary>Thrown when a provider name is unknown to the registry.</summary>
public sealed class ProviderNotFoundException(string providerName)
    : InvalidOperationException($"Provider '{providerName}' is not registered.");
