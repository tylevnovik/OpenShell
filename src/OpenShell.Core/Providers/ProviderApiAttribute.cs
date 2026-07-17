namespace OpenShell.Providers;

/// <summary>
/// Declares the versioning metadata of a Provider-facing API surface. Per ADR-0038 §3.
/// Apply to interfaces (and optionally methods/properties) to document the lifecycle:
/// when the API was introduced, when it was deprecated, when it will be removed, and its replacement.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ProviderApiAttribute : Attribute
{
    /// <summary>Provider API version in which this member was introduced, e.g. "1.0.0". Parsed at use site.</summary>
    public string? SinceVersion { get; init; }

    /// <summary>API version from which this member is considered deprecated. Per ADR-0038, the Deprecated phase lasts at least 2 milestones.</summary>
    public string? DeprecatedSince { get; init; }

    /// <summary>API version in which this member will be (or was) removed. After this version, providers using it will fail to compile/load.</summary>
    public string? RemovedIn { get; init; }

    /// <summary>Replacement API fully-qualified name, e.g. "OpenShell.Providers.IBatchItemProvider".</summary>
    public string? Replacement { get; init; }

    /// <summary>Optional human-readable migration notes shown by `dotnet openshell migrate`.</summary>
    public string? MigrationNotes { get; init; }

    /// <summary>Stability tier. Defaults to Stable; Preview/Experimental members are gated accordingly.</summary>
    public ProviderApiStability Stability { get; init; } = ProviderApiStability.Stable;

    /// <summary>Parse <see cref="SinceVersion"/> into a <see cref="Version"/>; null if unset or unparseable.</summary>
    public Version? ParsedSince() => SinceVersion is null ? null : Version.TryParse(SinceVersion, out var v) ? v : null;

    /// <summary>Parse <see cref="DeprecatedSince"/> into a <see cref="Version"/>; null if unset or unparseable.</summary>
    public Version? ParsedDeprecatedSince() => DeprecatedSince is null ? null : Version.TryParse(DeprecatedSince, out var v) ? v : null;

    /// <summary>Parse <see cref="RemovedIn"/> into a <see cref="Version"/>; null if unset or unparseable.</summary>
    public Version? ParsedRemovedIn() => RemovedIn is null ? null : Version.TryParse(RemovedIn, out var v) ? v : null;
}
