using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Remoting;

/// <summary>
/// ADR-0059 §7: 远程基础设施 DI 注册扩展。
/// 在 <c>Program.cs</c> 的 <c>ConfigureServices</c> 中调用 <see cref="AddRemoting"/> 一次,
/// 注册 <see cref="PSSessionManager"/> 为单例。
/// </summary>
public static class RemotingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0059 远程基础设施:
    /// <list type="bullet">
    ///   <item><see cref="PSSessionManager"/> (singleton, 跨命令共享会话表)。</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddRemoting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<PSSessionManager>();
        return services;
    }
}
