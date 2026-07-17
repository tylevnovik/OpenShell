using System.Reflection;

namespace OpenShell.Providers;

/// <summary>
/// Performs API version compatibility checks between a Provider's declared
/// <see cref="ProviderInfo.RequiredApiVersion"/> and the host's <see cref="ProviderApiVersion.Current"/>.
/// Per ADR-0038 §2 (compatibility matrix).
/// </summary>
public static class ApiCompatibilityChecker
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="provider"/> is loadable on this host,
    /// otherwise throws <see cref="ApiMismatchException"/> with a remediation hint.
    /// </summary>
    /// <exception cref="ApiMismatchException">Major version mismatch or required version older than supported.</exception>
    public static bool Verify(ProviderInfo provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = ProviderApiVersion.Current;
        var required = provider.RequiredApiVersion;

        // Experimental providers without a required version are rejected unless explicitly enabled
        // (ADR-0038 §6 + constraint: RequiredApiVersion 必须声明，否则视为 Experimental 拒绝加载).
        if (provider.ApiStability == ProviderApiStability.Experimental && required == ProviderApiVersion.Current)
        {
            // Experimental with default version — still loadable only when caller opts in; the loader
            // checks ApiStability separately. Here we only enforce version contract.
        }

        if (required.Major != host.Major)
        {
            var remediation = required.Major > host.Major
                ? $"升级 OpenShell 到 >= {required.Major}.0.0 或联系 provider 作者降低 API 依赖。"
                : $"Provider 依赖已弃用的 API v{required.Major}；联系 provider 作者升级到 v{host.Major} API。";
            throw new ApiMismatchException(provider, host, required, remediation);
        }

        if (required < ProviderApiVersion.MinimumSupported)
        {
            throw new ApiMismatchException(
                provider, host, required,
                $"Provider 要求 API {required}，低于主机最低支持 {ProviderApiVersion.MinimumSupported}。请升级 provider。");
        }

        return true;
    }

    /// <summary>
    /// Scans a provider type's implemented interfaces for <see cref="ProviderApiAttribute.DeprecatedSince"/>
    /// markers and returns those that are deprecated in the current API version. Per ADR-0038 §3.
    /// The host logs warnings for these at load time.
    /// </summary>
    public static IReadOnlyList<DeprecatedApiNotice> FindDeprecatedUsage(IProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = ProviderApiVersion.Current;
        var notices = new List<DeprecatedApiNotice>();
        var type = provider.GetType();
        foreach (var iface in type.GetInterfaces())
        {
            var attr = iface.GetCustomAttribute<ProviderApiAttribute>();
            var since = attr?.ParsedDeprecatedSince();
            var removedIn = attr?.ParsedRemovedIn();
            if (since is { } depSince && depSince <= host && (removedIn is null || removedIn > host))
            {
                notices.Add(new DeprecatedApiNotice(
                    iface.FullName ?? iface.Name,
                    depSince,
                    removedIn,
                    attr!.Replacement,
                    attr.MigrationNotes));
            }
        }
        return notices;
    }
}

/// <summary>Describes a deprecated API surface that a provider still consumes. Per ADR-0038 §3.</summary>
public sealed record DeprecatedApiNotice(
    string InterfaceName,
    Version DeprecatedSince,
    Version? RemovedIn,
    string? Replacement,
    string? MigrationNotes);
