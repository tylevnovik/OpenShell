using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Security;

/// <summary>
/// ADR-0054 ExecutionPolicy DI 注册扩展。
/// 在 <c>Program.cs</c> 的 <c>ConfigureServices</c> 中调用 <see cref="AddExecutionPolicy"/> 一次,
/// 注册 <see cref="IExecutionPolicyService"/> 为单例。
/// </summary>
public static class ExecutionPolicyServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0054 脚本执行策略服务:
    /// <list type="bullet">
    ///   <item><see cref="IExecutionPolicyService"/> → <see cref="ExecutionPolicyService"/> (singleton, 注入 IConfigurationService + ISignatureVerifier)。</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddExecutionPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IExecutionPolicyService, ExecutionPolicyService>();
        return services;
    }
}
