using System.Reflection;
using OpenShell.Operations;
using OpenShell.Providers;

namespace OpenShell.Plugins;

/// <summary>
/// 插件加载器抽象。Per ADR-0016: 基于 <see cref="System.Runtime.Loader.AssemblyLoadContext"/> 的第三方 Provider 加载机制。
/// 负责加载、卸载、查询插件；加载/卸载事件供宿主观察生命周期。
/// </summary>
public interface IPluginLoader
{
    /// <summary>当前已加载的插件列表（快照）。</summary>
    IReadOnlyList<LoadedPlugin> Loaded { get; }

    /// <summary>
    /// 加载一个插件。Per ADR-0016 §2: 校验 manifest → 创建 collectible ALC →
    /// 加载程序集 → 通过 <see cref="IPluginEntryPoint"/> 暴露 providers/commands。
    /// 加载失败抛 <see cref="PluginLoadException"/>，由调用方捕获以保证主程序不受影响。
    /// </summary>
    LoadedPlugin Load(PluginManifest manifest);

    /// <summary>
    /// 卸载指定名称的插件 (同步, 简化版)。Per ADR-0016 §3: 反注册 providers/commands → 调用 Shutdown → 卸载 ALC。
    /// <b>不</b>等待 in-flight 操作完成; 适用于已知无活跃操作的关闭路径。
    /// 推荐使用 <see cref="UnloadAsync"/> 进行完整卸载。
    /// 未找到时返回 false，可重入（重复调用安全）。
    /// </summary>
    bool Unload(string name);

    /// <summary>
    /// 卸载指定名称的插件 (完整版, 异步)。Per ADR-0016 §3 完整流程:
    /// <list type="number">
    /// <item>反注册 providers / commands (停止新调用)。</item>
    /// <item>等待所有 in-flight 操作完成 (通过 <see cref="IOperationTracker"/>), 带 <paramref name="cancellationToken"/> 超时。</item>
    /// <item>调用 <see cref="IPluginEntryPoint.Shutdown"/> 钩子 (如实现 IAsyncDisposable 则 await DisposeAsync)。</item>
    /// <item>卸载 ALC (PluginCollectibleContext.Dispose → ALC.Unload)。</item>
    /// <item>等待 GC 回收 ALC (最多 30 次 × 100ms 循环, 未回收时仅 log warning 不报错)。</item>
    /// </list>
    /// 未找到时返回 false, 可重入 (重复调用安全)。
    /// </summary>
    /// <param name="name">插件名。</param>
    /// <param name="cancellationToken">取消令牌; 取消时中止等待 in-flight, 但已反注册的 providers/commands 不恢复 (插件不可用)。</param>
    /// <returns>true 表示卸载完成; false 表示插件未找到或等待 in-flight 超时/取消 (检查日志)。</returns>
    ValueTask<bool> UnloadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>按名称查找已加载插件。Per ADR-0016.</summary>
    bool TryGet(string name, out LoadedPlugin? plugin);

    /// <summary>插件加载完成事件。</summary>
    event EventHandler<LoadedPlugin>? PluginLoaded;

    /// <summary>插件卸载完成事件。</summary>
    event EventHandler<LoadedPlugin>? PluginUnloaded;
}

/// <summary>
/// 已加载插件的运行时快照。Per ADR-0016. 跨 ALC 边界对象均为 OpenShell.Core 内的接口/record。
/// </summary>
public sealed record LoadedPlugin
{
    /// <summary>插件名（唯一标识，与 manifest 一致）。</summary>
    public required string Name { get; init; }

    /// <summary>插件版本。</summary>
    public required Version Version { get; init; }

    /// <summary>程序集绝对路径。</summary>
    public required string AssemblyPath { get; init; }

    /// <summary>承载该插件的 collectible AssemblyLoadContext。</summary>
    public required PluginCollectibleContext Context { get; init; }

    /// <summary>该插件注册的 Provider 实例列表（用于反注册）。</summary>
    public required IReadOnlyList<IProvider> Providers { get; init; }

    /// <summary>该插件注册的命令类型列表（用于反注册）。</summary>
    public required IReadOnlyList<Type> CommandTypes { get; init; }

    /// <summary>加载时间戳。</summary>
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 插件清单。由 <c>plugin.manifest.json</c> 反序列化得到。Per ADR-0016 §4.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>插件名（唯一标识）。</summary>
    public required string Name { get; init; }

    /// <summary>插件版本。</summary>
    public required Version Version { get; init; }

    /// <summary>
    /// 程序集路径。可为相对路径，由 <see cref="PluginManifestLoader"/> 相对 manifest 文件解析为绝对路径。
    /// </summary>
    public required string AssemblyPath { get; init; }

    /// <summary>
    /// 入口类型全名（实现了 <see cref="IPluginEntryPoint"/> 的类的全名，含命名空间）。
    /// </summary>
    public required string EntryType { get; init; }
}

/// <summary>
/// 插件入口点契约。Per ADR-0016. 第三方插件需实现此接口以暴露 providers 与 commands。
/// 跨 ALC 边界对象均为 OpenShell.Core 内的接口/record。
/// </summary>
public interface IPluginEntryPoint
{
    /// <summary>返回该插件提供的 Provider 实例列表。</summary>
    IReadOnlyList<IProvider> GetProviders();

    /// <summary>返回该插件提供的命令类型列表（带 [Verb] 特性的 sealed 类）。</summary>
    IReadOnlyList<Type> GetCommandTypes();

    /// <summary>初始化钩子。宿主在注册 providers/commands 之前调用。</summary>
    void Initialize(IServiceProvider services);

    /// <summary>卸载前的清理钩子。宿主在反注册 providers/commands 之后、卸载 ALC 之前调用。</summary>
    void Shutdown();
}

/// <summary>
/// 插件加载异常。Per ADR-0016 §7: 加载失败抛此异常，不影响其他插件或主程序。
/// </summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message) { }
    public PluginLoadException(string message, Exception innerException) : base(message, innerException) { }
}
