namespace OpenShell.Providers;

/// <summary>
/// Capability flags declared by an <see cref="IProvider"/>. Per ADR-0001, capabilities are also
/// expressed via the interfaces the provider implements; the flags double as a fast dispatch hint.
/// </summary>
[Flags]
public enum ProviderCapability
{
    None        = 0,
    Item        = 1 << 0,
    Container   = 1 << 1,
    Navigation  = 1 << 2,
    Content     = 1 << 3,
    ContentWrite = 1 << 4,
    Property    = 1 << 5,
    Security    = 1 << 6,
    Drive       = 1 << 7,
    PropertyWrite = 1 << 8,
}
