namespace OpenShell.Packaging.Registry;

/// <summary>
/// Provider 注册源。Per ADR-0039 §3.
/// 一个注册源是 <c>~/.openshell/registries.toml</c> 中配置的 <c>[[source]]</c> 条目,
/// 指向一个 HTTP/file REST API 端点 (见 <see cref="RegistryClient"/>)。
/// </summary>
public sealed record ProviderSource
{
    /// <summary>源唯一名称 (如 <c>official</c> / <c>private-company</c> / <c>local-dev</c>)。</summary>
    public required string Name { get; init; }

    /// <summary>源 URL。HTTP(s) 或 file://。结尾 <c>/</c> 可选 (客户端规范化处理)。</summary>
    public required string Url { get; init; }

    /// <summary>
    /// 优先级, 数字越小越优先。Per ADR-0039 §3.
    /// 当多个源都提供同名包时, 取优先级最高 (数字最小) 的源。
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// 是否受信任源。Per ADR-0039 §3 / §8.
    /// 官方/本地源 trusted=true 时放宽签名校验; 私有源 trusted=false 强制校验。
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>
    /// 认证 token 引用, 形如 <c>env:CORP_REGISTRY_TOKEN</c>。Per ADR-0039 §3.
    /// 客户端解析 <c>env:</c> 前缀, 从对应环境变量读取实际 token, 作为 Authorization 头发送。
    /// 为 null 表示匿名访问。
    /// </summary>
    public string? Auth { get; init; }

    /// <summary>规范化 URL, 末尾保证有一个 <c>/</c> (便于拼接相对路径)。</summary>
    public string NormalizedUrl => Url.EndsWith('/') ? Url : Url + "/";
}
