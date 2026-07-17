namespace OpenShell.Providers;

/// <summary>
/// The OpenShell Core Provider API version this host implements. Per ADR-0038 §1.
/// Decoupled from the OpenShell.Core NuGet package version: API version changes only when
/// a capability interface signature changes (added/removed/renamed members).
/// </summary>
public static class ProviderApiVersion
{
    /// <summary>Current Provider API version implemented by this host. Per ADR-0038, v1.0.0.</summary>
    public static readonly Version Current = new(1, 0, 0);

    /// <summary>Minimum host version a Provider may target. Older RequiredApiVersion values are rejected as too old.</summary>
    public static readonly Version MinimumSupported = new(1, 0, 0);
}
