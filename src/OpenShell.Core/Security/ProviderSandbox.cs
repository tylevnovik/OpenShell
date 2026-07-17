namespace OpenShell.Security;

/// <summary>
/// Provider 沙箱级别。Per ADR-0036 §6.
/// 第三方 Provider 加载时通过 <c>[assembly: ProviderAssembly(...)]</c> 声明。
/// </summary>
public enum SandboxLevel
{
    /// <summary>无沙箱, 完全信任 (不推荐用于第三方 Provider)。</summary>
    None,

    /// <summary>只读: 禁止任何写接口调用。</summary>
    ReadOnly,

    /// <summary>受限: 限制可读/可写路径、禁止网络/进程生成。</summary>
    Restricted,

    /// <summary>完全: 信任 Provider 内的任意操作 (默认值)。</summary>
    Full,
}

/// <summary>
/// Provider 沙箱能力范围声明。Per ADR-0036 §6.
/// 第三方 Provider 加载时声明允许访问的路径集合与运行时能力。
/// 运行时拦截越界访问 (声明 ReadOnly 但调写接口 → 拒绝)。
/// </summary>
/// <remarks>
/// ADR-0036 §6 已实现: <see cref="OpenShell.Plugins.PluginLoader"/> 加载时读取 [assembly: ProviderAssembly] 特性,
/// 映射 SandboxLevel → ProviderSandbox, 通过 <see cref="SandboxContext"/> 传播。
/// ADR-0036 §11 已实现: <see cref="SandboxAwareDelegatingHandler"/> 拦截 HttpClient 网络访问。
/// ADR-0036 §12 已实现: <see cref="ProcessSpawnGuard"/> 强制进程生成权限。
/// 路径级限制 (AllowedReadPaths/AllowedWritePaths) 为 advisory, 不做 OS 级拦截; 沙箱元数据可用于审计。
/// </remarks>
public sealed record ProviderSandbox(
    IReadOnlySet<string> AllowedReadPaths,
    IReadOnlySet<string> AllowedWritePaths,
    bool NetworkAccess,
    bool ProcessSpawn)
{
    /// <summary>无限制的默认沙箱 (用于内置 Provider)。</summary>
    public static ProviderSandbox Full { get; } = new(
        AllowedReadPaths: new HashSet<string>(0),
        AllowedWritePaths: new HashSet<string>(0),
        NetworkAccess: true,
        ProcessSpawn: true);
}
