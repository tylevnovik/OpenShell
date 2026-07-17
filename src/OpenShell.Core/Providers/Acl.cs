namespace OpenShell.Providers;

/// <summary>
/// Minimal security descriptor placeholder. Real implementation will be supplied by the Security provider work.
/// </summary>
public sealed record Acl(string Owner, string? Group = null, IReadOnlyList<string>? Rules = null);
