#nullable enable
// ADR-0056 §3 脚本模块系统 DI 注册扩展。
// 在 Program.cs / AppBuilder.cs / TestHostBuilder.Build() 中调用 AddScriptModules() 一次,
// 注册 ModuleRegistry 为单例。Evaluator 通过 Host.Services.GetService<ModuleRegistry>() 解析。

using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Modules;

/// <summary>
/// ADR-0056 脚本模块系统 DI 注册扩展。
/// </summary>
public static class ModuleServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0056 脚本模块注册表:
    /// <list type="bullet">
    ///   <item><see cref="ModuleRegistry"/> (singleton) — 缓存已加载的 .osh 模块，供 import/export 使用。</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddScriptModules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ModuleRegistry>();
        return services;
    }
}
