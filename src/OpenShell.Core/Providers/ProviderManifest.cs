using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShell.Providers;

/// <summary>
/// Provider package manifest, serialised to <c>openshell.provider.json</c> at the root of a
/// <c>.osp</c> package (ADR-0039 §2) or inside a plugin directory. Carries the full metadata
/// required for dependency resolution, signature verification, and API compatibility checks (ADR-0038 §4).
/// </summary>
public sealed record ProviderManifest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("requiredApiVersion")]
    public string RequiredApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("apiStability")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderApiStability ApiStability { get; init; } = ProviderApiStability.Stable;

    [JsonPropertyName("authors")]
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    [JsonPropertyName("owners")]
    public IReadOnlyList<string> Owners { get; init; } = Array.Empty<string>();

    [JsonPropertyName("repository")]
    public string? Repository { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("licenseUrl")]
    public string? LicenseUrl { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ProviderDependency> Dependencies { get; init; } = Array.Empty<ProviderDependency>();

    [JsonPropertyName("minimumHostVersion")]
    public string? MinimumHostVersion { get; init; }

    [JsonPropertyName("supportedPlatforms")]
    public IReadOnlyList<string> SupportedPlatforms { get; init; } = Array.Empty<string>();

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; init; }

    /// <summary>Optional JSON Schema describing provider-specific config (used by GUI config panels).</summary>
    [JsonPropertyName("configSchema")]
    public JsonElement? ConfigSchema { get; init; }

    /// <summary>Parse a JSON document into a <see cref="ProviderManifest"/>. Per ADR-0038 §4 / ADR-0039 §2.</summary>
    public static ProviderManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var manifest = JsonSerializer.Deserialize<ProviderManifest>(json, ManifestJsonOptions.Default)
            ?? throw new JsonException("Provider manifest JSON deserialised to null.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>Validate required fields and version formats. Throws on invalid manifest.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ProviderManifestException("Manifest 'name' is required.");
        if (string.IsNullOrWhiteSpace(Version))
            throw new ProviderManifestException("Manifest 'version' is required.");
        if (string.IsNullOrWhiteSpace(RequiredApiVersion))
            throw new ProviderManifestException("Manifest 'requiredApiVersion' is required (per ADR-0038 constraint).");
        if (!System.Version.TryParse(Version, out _))
            throw new ProviderManifestException($"Manifest 'version' ({Version}) is not a valid SemVer.");
        if (!System.Version.TryParse(RequiredApiVersion, out _))
            throw new ProviderManifestException($"Manifest 'requiredApiVersion' ({RequiredApiVersion}) is not a valid version.");
    }

    /// <summary>Convert to the in-memory <see cref="ProviderInfo"/> record used by the registry.</summary>
    public ProviderInfo ToProviderInfo()
    {
        Validate();
        return new ProviderInfo
        {
            Name = Name,
            Version = System.Version.Parse(Version),
            RequiredApiVersion = System.Version.Parse(RequiredApiVersion),
            ApiStability = ApiStability,
            Description = Description,
            Author = Authors.Count > 0 ? Authors[0] : null,
        };
    }
}

/// <summary>A single dependency entry in a provider manifest. Per ADR-0039 §2.</summary>
public sealed record ProviderDependency
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>NuGet-style version range, e.g. ">= 1.0.0 &lt; 2.0.0" or "[1.0,2.0)".</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>"provider" (OpenShell Provider package) or "external" (NuGet library). Defaults to "provider".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "provider";
}

/// <summary>Thrown when a provider manifest fails validation. Per ADR-0038 §4.</summary>
public sealed class ProviderManifestException : Exception
{
    public ProviderManifestException(string message) : base(message) { }
    public ProviderManifestException(string message, Exception inner) : base(message, inner) { }
}

internal static class ManifestJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
