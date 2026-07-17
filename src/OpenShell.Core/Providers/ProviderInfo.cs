namespace OpenShell.Providers;

/// <summary>
/// Static metadata describing a provider. Per ADR-0005 providers expose this via assembly attribute;
/// per ADR-0038 it also carries the API compatibility contract.
/// </summary>
public sealed record ProviderInfo
{
    /// <summary>Provider short name, e.g. "fs", "zip", "reg", "s3". Used as the path namespace prefix.</summary>
    public required string Name { get; init; }

    /// <summary>Provider implementation version (independent of API version). e.g. 1.2.0.</summary>
    public required Version Version { get; init; }

    /// <summary>
    /// Provider API version this implementation was built against. Per ADR-0038 §1, the major version
    /// must equal <see cref="ProviderApiVersion.Current"/>'s major version; minor/patch differences are forward-compatible.
    /// Defaults to the current host API version when omitted (built-in providers).
    /// </summary>
    public Version RequiredApiVersion { get; init; } = ProviderApiVersion.Current;

    /// <summary>API stability tier declared by the provider. Defaults to Stable.</summary>
    public ProviderApiStability ApiStability { get; init; } = ProviderApiStability.Stable;

    public string? Description { get; init; }
    public string? Author { get; init; }
}
