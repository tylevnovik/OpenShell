using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Clipboard;

/// <summary>
/// ADR-0029 剪贴板运行时 DI 注册扩展。Per ADR-0029 §1 / §13.
/// 注册进程内 <see cref="IClipboardService"/> (默认 <see cref="InMemoryClipboardService"/>);
/// 历史为可选功能, 经 <c>enableHistory</c> 参数显式开启。
/// </summary>
/// <remarks>
/// 约定: GUI host 若需 OS 剪贴板互操作 (AvaloniaClipboardService), 应在调用本扩展后
/// 再次注册 <see cref="IClipboardService"/> 覆盖默认实现 (MS DI 取最后注册项);
/// 此时 <see cref="ClipboardHistoryService"/> 构造时解析到的将是 GUI 实现并订阅其
/// <see cref="IClipboardService.ClipboardChanged"/> 事件。CommandDispatchingDragDropService
/// 需主机特定命令分发委托, 由各 host 自行注册 IDragDropService。
/// </remarks>
public static class ClipboardServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ADR-0029 剪贴板运行时核心服务。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="enableHistory">
    /// 是否启用剪贴板历史 (ADR-0029 §13, 默认关闭)。开启后注册
    /// <see cref="IClipboardHistoryService"/> → <see cref="ClipboardHistoryService"/>,
    /// 订阅 <see cref="IClipboardService.ClipboardChanged"/> 自动追加历史。
    /// </param>
    /// <returns>原 <paramref name="services"/> 引用, 便于链式调用。</returns>
    public static IServiceCollection AddClipboardRuntime(
        this IServiceCollection services,
        bool enableHistory = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 默认进程内剪贴板 (CLI host / 单元测试)。GUI host 可后续覆盖为 AvaloniaClipboardService。
        services.AddSingleton<IClipboardService, InMemoryClipboardService>();

        if (enableHistory)
        {
            // 历史服务订阅 IClipboardService.ClipboardChanged; 解析时取最后注册的 IClipboardService。
            services.AddSingleton<IClipboardHistoryService, ClipboardHistoryService>();
        }

        return services;
    }
}
