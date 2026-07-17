using System.Text.Json.Serialization;

namespace OpenShell.Packaging.Registry;

/// <summary>
/// 注册源返回的包元信息。Per ADR-0039 §4.
/// 对应 <c>GET /v1/packages/{name}</c> 响应: 包名 + 所有版本 + latest 指针。
/// </summary>
public sealed record PackageInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("versions")]
    public IReadOnlyList<PackageVersionInfo> Versions { get; init; } = Array.Empty<PackageVersionInfo>();

    /// <summary>最新稳定版 (源返回或客户端从 Versions 推断)。</summary>
    [JsonPropertyName("latest")]
    public string? Latest { get; init; }

    /// <summary>下载计数 (部分源可能不返回此字段)。</summary>
    [JsonPropertyName("downloads")]
    public long? Downloads { get; init; }

    /// <summary>包简短描述 (取最新版 manifest)。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// 单个包版本的元信息。Per ADR-0039 §4 / §2.
/// 对应 <c>GET /v1/packages/{name}/{version}</c> 或 <c>GET /v1/packages/{name}</c> 中 versions 数组元素。
/// </summary>
public sealed record PackageVersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; init; }

    [JsonPropertyName("stability")]
    public string? Stability { get; init; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}
