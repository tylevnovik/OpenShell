using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Security;

/// <summary>
/// ADR-0036 安全沙箱运行时 DI 注册扩展。
/// 在 <c>Program.cs</c> 的 <c>ConfigureServices</c> 中调用 <see cref="AddSecuritySandboxRuntime"/> 一次,
/// 注册审计保留期清理、安全密码提示器、沙箱感知 HTTP 处理器等服务。
/// </summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0036 安全沙箱运行时服务:
    /// <list type="bullet">
    ///   <item><see cref="AuditRetentionService"/> (IHostedService, 每日 30 天保留期清理)。</item>
    ///   <item><see cref="ISecurePasswordPrompter"/> → <see cref="ConsoleSecurePasswordPrompter"/> (paranoid 模式 PIN 提示)。</item>
    ///   <item><see cref="SandboxAwareDelegatingHandler"/> (transient, 供 IHttpClientFactory 管道使用)。</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddSecuritySandboxRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ADR-0036 §5: 审计日志 30 天保留期清理 (每日执行, 启动时立即执行一次)。
        services.AddHostedService<AuditRetentionService>();

        // ADR-0036 §14: paranoid 模式密码提示器 (控制台实现, 可被 GUI 宿主覆盖注册)。
        services.AddSingleton<ISecurePasswordPrompter, ConsoleSecurePasswordPrompter>();

        // ADR-0036 §11: 沙箱感知 HTTP 委托处理器 (供 IHttpClientFactory 管道 AddHttpMessageHandler 使用)。
        services.AddTransient<SandboxAwareDelegatingHandler>();

        return services;
    }

    /// <summary>
    /// 注册 <see cref="SandboxAwareDelegatingHandler"/> 为 transient, 供
    /// <c>AddHttpClient(...).AddHttpMessageHandler&lt;SandboxAwareDelegatingHandler&gt;()</c> 使用。
    /// (已包含在 <see cref="AddSecuritySandboxRuntime"/> 中; 此方法供仅需 HTTP 拦截器的场景单独调用。)
    /// </summary>
    public static IServiceCollection AddSandboxAwareHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<SandboxAwareDelegatingHandler>();
        return services;
    }
}
