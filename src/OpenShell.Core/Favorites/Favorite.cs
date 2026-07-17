namespace OpenShell.Favorites;

/// <summary>
/// 收藏夹条目。Per ADR-0028 §6.
/// <para>
/// <see cref="Path"/> 为 provider-qualified 路径, 例如 <c>fs::C:/Users/me/Projects</c>
/// 或 <c>s3://my-bucket</c>。
/// </para>
/// </summary>
/// <param name="Name">用户可读的收藏名称 (大小写不敏感匹配)。</param>
/// <param name="Path">Provider-qualified 目标路径。</param>
public sealed record Favorite(string Name, string Path);
