using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Sessions;

/// <summary>
/// ADR-0034 会话运行时 DI 注册扩展。
/// 在 <c>Program.cs</c> 的 <c>ConfigureServices</c> 中调用 <see cref="AddSessionRuntime"/> 一次,
/// 注册会话自动保存服务 (<see cref="SessionAutoSaveService"/>) 等会话运行时依赖。
/// </summary>
/// <remarks>
/// 注意: <see cref="ISessionService"/> 本身 (默认实现 <see cref="JsonSessionService"/>) 需由调用方单独注册,
/// 因不同宿主可能传入不同的 baseDir。此扩展仅注册依赖 ISessionService 的后台服务。
/// </remarks>
public static class SessionServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0034 会话运行时服务:
    /// <list type="bullet">
    ///   <item><see cref="SessionAutoSaveService"/> (IHostedService, 每 30s 自动保存当前会话)。Per ADR-0034 §3.</item>
    /// </list>
    /// 前置条件: <see cref="ISessionService"/> 已注册 (由调用方在调用此方法前添加)。
    /// </summary>
    public static IServiceCollection AddSessionRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ADR-0034 §3: 30s 定期自动保存 (IHostedService)。
        services.AddHostedService<SessionAutoSaveService>();

        return services;
    }

    /// <summary>
    /// 注册 ADR-0034 §9 会话跨机器同步服务 (SessionSyncService)。
    /// 调用方需同时注册 <see cref="ISessionSyncProvider"/> (如 <see cref="WebDavSessionSyncProvider"/>)。
    /// </summary>
    /// <remarks>
    /// 同步默认关闭 (Per ADR-0034 §13)。仅在配置 <c>SyncProvider != "none"</c> 时调用此方法。
    /// </remarks>
    public static IServiceCollection AddSessionSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SessionSyncService>();

        return services;
    }
}
