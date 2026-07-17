namespace OpenShell.Providers;

/// <summary>
/// Stability tier for a Provider API surface. Per ADR-0038 §6.
/// </summary>
public enum ProviderApiStability
{
    /// <summary>GA interface: no breaking changes within the same major version.</summary>
    Stable,

    /// <summary>Preview interface: may break in a minor version; marked with [ProviderApi].</summary>
    Preview,

    /// <summary>Experimental: may break in any version; not loaded without --enable-experimental.</summary>
    Experimental,
}
