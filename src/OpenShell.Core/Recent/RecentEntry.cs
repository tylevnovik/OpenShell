namespace OpenShell.Recent;

/// <summary>
/// 最近访问条目。Per ADR-0028 §7.
/// <para>
/// <see cref="Path"/> 为 provider-qualified 路径, 例如 <c>fs::C:/Users/me</c>。
/// <see cref="Timestamp"/> 为最近一次访问时间 (UTC)。
/// </para>
/// </summary>
/// <param name="Path">Provider-qualified 路径。</param>
/// <param name="Timestamp">最近一次访问的 UTC 时间戳。</param>
public sealed record RecentEntry(string Path, DateTimeOffset Timestamp);
