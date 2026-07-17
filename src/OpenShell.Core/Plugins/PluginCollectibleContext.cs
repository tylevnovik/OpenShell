using System.Reflection;
using System.Runtime.Loader;

namespace OpenShell.Plugins;

/// <summary>
/// 插件专用的 collectible AssemblyLoadContext。Per ADR-0016 §1.
/// <list type="bullet">
/// <item><c>isCollectible: true</c> 才能 <see cref="System.Runtime.Loader.AssemblyLoadContext.Unload"/>（卸载后 GC 可回收 ALC 内类型/实例）。</item>
/// <item>共享框架程序集（<c>OpenShell.*</c> / <c>System.*</c> / <c>Microsoft.Extensions.*</c>）走默认 ALC，
/// 避免重复加载导致 <c>IProvider</c> 等接口在不同 ALC 中成为不同 Type 实例。</item>
/// <item>插件私有依赖（如 <c>AWSSDK.S3</c>）走 <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> 自身目录解析。</item>
/// </list>
/// </summary>
public sealed class PluginCollectibleContext : AssemblyLoadContext, IDisposable
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginPath;
    private bool _unloaded;

    /// <param name="pluginPath">插件主程序集的绝对路径，用于解析同目录下的 .deps.json。</param>
    /// <param name="name">ALC 名称，建议为 <c>plugin::{name}</c> 形式以便诊断。</param>
    public PluginCollectibleContext(string pluginPath, string name) : base(name, isCollectible: true)
    {
        _pluginPath = pluginPath ?? throw new ArgumentNullException(nameof(pluginPath));
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    /// <summary>
    /// 程序集解析钩子。返回 null 表示由父 ALC（通常为默认 ALC）处理。
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1. OpenShell.Core 永远走默认 ALC（保证 IProvider 等接口是同一 Type 实例）。
        // 2. 共享框架程序集也走默认 ALC，禁止插件私有副本。
        if (IsSharedFramework(assemblyName.Name))
            return null;

        // 3. 插件私有依赖走自身目录解析。
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    /// <summary>非托管 DLL 解析钩子。</summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    /// <summary>
    /// 判断程序集是否属于共享框架（走默认 ALC，不进入插件 ALC）。
    /// 包含 OpenShell.* 命名空间以及 BCL / MS Extensions 常见共享库。
    /// </summary>
    private static bool IsSharedFramework(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // OpenShell.* 全部共享（Core / Providers.* 等）。
        if (name!.StartsWith("OpenShell", StringComparison.Ordinal))
            return true;

        // BCL / 运行时共享程序集。
        if (name.StartsWith("System", StringComparison.Ordinal))
            return true;
        if (name == "mscorlib" || name == "netstandard")
            return true;

        // MS Extensions 共享库（避免不同插件加载不同版本导致 DI 容器行为不一致）。
        if (name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal))
            return true;
        // Microsoft.Extensions.Logging.Abstractions 等已包含在上一行；补充常见非前缀形式：
        if (name == "Microsoft.CodeAnalysis" || name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            return false;   // Roslyn 不强制共享，允许插件私有版本

        return false;
    }

    /// <summary>
    /// 加载插件主程序集。Per ADR-0016 §2 第 3 步。
    /// </summary>
    public Assembly LoadPluginAssembly(string assemblyPath)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
            throw new PluginLoadException($"Plugin assembly not found: {path}");
        return LoadFromAssemblyPath(path);
    }

    /// <summary>
    /// 卸载 ALC。可重入：重复调用安全。Per ADR-0016 §3 第 4 步。
    /// </summary>
    public void Dispose()
    {
        if (_unloaded) return;
        _unloaded = true;
        try
        {
            Unload();
        }
        catch (Exception)
        {
            // Unload 在某些极端状态（如已 unload）下可能抛，吞掉以保证可重入。
        }
    }
}
