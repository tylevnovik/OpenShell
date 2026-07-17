using Microsoft.Extensions.DependencyInjection;
using OpenShell.Events;
using OpenShell.Providers;

namespace OpenShell.Operations;

/// <summary>
/// ADR-0044 操作运行时 DI 注册扩展。
/// <para>
/// 注册 <see cref="ITaskCenter"/> → <see cref="InMemoryTaskCenter"/> (Per ADR-0044 §1),
/// 并重新装配 <see cref="IOperationEngine"/> 装饰器链, 把 <see cref="ITaskCenter"/> 注入到
/// 内层 <see cref="OperationEngine"/> 构造函数 (Per ADR-0044 §2: BeginXxx 需要 ITaskCenter)。
/// </para>
/// <para>
/// 约定: 调用方需先注册 <see cref="IProviderRegistry"/>, <see cref="ITrashService"/>,
/// <see cref="IOperationJournal"/>, <see cref="IOperationTracker"/> (以及可选的 <see cref="IEventBus"/>)。
/// 本扩展覆盖 <see cref="IOperationEngine"/> 绑定 (MS DI 取最后注册项)。
/// </para>
/// </summary>
public static class OperationsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0044 操作运行时: 任务中心 + 带 ITaskCenter 的操作引擎装饰器链。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>原 <paramref name="services"/> 引用, 便于链式调用。</returns>
    public static IServiceCollection AddOperationsRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ITaskCenter → InMemoryTaskCenter (注入可选 IEventBus 用于 OperationCompletedEvent 发布)。
        // Per ADR-0044 §1 + §7.
        services.AddSingleton<ITaskCenter>(sp =>
            new InMemoryTaskCenter(sp.GetService<IEventBus>()));

        // 重新装配 IOperationEngine 装饰器链: JournalingOperationEngine(TrackingOperationEngine(OperationEngine))。
        // 与 Program.cs 原有装配一致, 区别在于 OperationEngine 构造函数新增 ITaskCenter 参数。
        // Per ADR-0044 §2: BeginXxx 方法需要 ITaskCenter 来注册任务句柄。
        services.AddSingleton<IOperationEngine>(sp =>
        {
            var providers = sp.GetRequiredService<IProviderRegistry>();
            var trash = sp.GetService<ITrashService>();
            var taskCenter = sp.GetRequiredService<ITaskCenter>();
            var tracker = sp.GetRequiredService<IOperationTracker>();
            var journal = sp.GetRequiredService<IOperationJournal>();

            var engine = new OperationEngine(providers, trash, taskCenter);
            var tracking = new TrackingOperationEngine(engine, tracker);
            var journaling = new JournalingOperationEngine(tracking, journal);
            return journaling;
        });

        return services;
    }
}
