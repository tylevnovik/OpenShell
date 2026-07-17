namespace OpenShell.Providers;

/// <summary>
/// Raised by <see cref="ApiCompatibilityChecker"/> when a Provider's <see cref="ProviderInfo.RequiredApiVersion"/>
/// is incompatible with the host's <see cref="ProviderApiVersion.Current"/>. Per ADR-0038 §2.
/// </summary>
public sealed class ApiMismatchException : Exception
{
    public ProviderInfo ProviderInfo { get; }
    public Version HostApiVersion { get; }
    public Version RequiredApiVersion { get; }

    /// <summary>Human-readable remediation hint, e.g. "升级 OpenShell 到 >= 2.0.0" or "联系 provider 作者升级到 v1 API".</summary>
    public string Remediation { get; }

    public ApiMismatchException(
        ProviderInfo providerInfo,
        Version hostApiVersion,
        Version requiredApiVersion,
        string remediation)
        : base(
            $"Provider '{providerInfo.Name}' requires API version {requiredApiVersion} " +
            $"but host provides {hostApiVersion}. {remediation}")
    {
        ProviderInfo = providerInfo;
        HostApiVersion = hostApiVersion;
        RequiredApiVersion = requiredApiVersion;
        Remediation = remediation;
    }
}
