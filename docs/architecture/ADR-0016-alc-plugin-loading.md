# ADR-0016: ALC 插件加载完整实现（含卸载与依赖隔离）

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M4
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0005 (ALC 加载占位), ADR-0001 (能力接口)
- **Implementation Status**: 完整实现 (M4, 2026-07-08). §1 PluginCollectibleContext, §2 PluginLoader.Load, §3 完整 UnloadAsync (含 IOperationTracker + GC 等待), §4 PluginManifestLoader, §6 内置/第三方统一, §7 ALC 隔离实现依赖冲突隔离, §8 PluginHotReloadService (FileSystemWatcher + debounce + 配置开关 PluginWatch/PluginHotReload).

## Context

ADR-0005 在 M0 已确定使用 `AssemblyLoadContext` 作为插件加载机制，但仅做了"加载"占位，未实现：

- 第三方 Provider 以独立 NuGet 包形式分发，需扫描 `~/.openshell/providers/` 目录加载
- 不同 Provider 可能依赖不同版本的同一基础库（如 `AWSSDK.S3` v3.5 vs v3.7），需依赖隔离
- 开发期"改代码 → 重新加载 Provider"无需重启 host
- 运行时升级 Provider（如安全补丁）可热替换不停服
- 卸载后 GC 必须回收 ALC 内的所有类型与实例

需要解决：

1. **依赖解析**：Provider 私有依赖走自己的 `.deps.json`，共享依赖（`OpenShell.Core`）走默认 ALC
2. **类型边界**：跨 ALC 边界对象必须是 `OpenShell.Core` 内的接口/record
3. **卸载安全**：Provider 不能持有非托管资源、不能创建无法回收的线程
4. **生命周期**：注册 → 初始化 → 使用 → 卸载 → GC
5. **错误恢复**：插件加载失败不影响主程序
6. **版本校验**：插件声明的 `OpenShell.Core` 版本必须兼容

## Decision

### 1. 完整 ProviderLoadContext

```csharp
public sealed class ProviderLoadContext : AssemblyLoadContext, IDisposable
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _providerName;
    public string ProviderName => _providerName;
    public bool IsLoaded { get; private set; }

    public ProviderLoadContext(string pluginPath, string providerName)
        : base(name: $"provider::{providerName}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _providerName = providerName;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1. OpenShell.Core 永远走默认 ALC（保证 IProvider 等接口是同一实例）
        if (assemblyName.Name == "OpenShell.Core")
            return null;   // null = 让默认 ALC 处理

        // 2. 共享依赖（System.*、Microsoft.Extensions.* 等）也走默认 ALC
        if (IsSharedFramework(assemblyName.Name))
            return null;

        // 3. Provider 私有依赖走自己的目录解析
        return _resolver.ResolveAssemblyToAssembly(assemblyName);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        => _resolver.ResolveUnmanagedDllToUnmanagedDll(unmanagedDllName);

    public Assembly LoadProviderAssembly(string assemblyPath)
    {
        var asm = LoadFromAssemblyPath(assemblyPath);
        IsLoaded = true;
        return asm;
    }

    public void Dispose()
    {
        if (IsLoaded)
        {
            Unload();
            IsLoaded = false;
        }
    }
}
```

### 2. 插件加载流程

```csharp
public sealed class PluginLoader
{
    public LoadedPlugin Load(string pluginPath)
    {
        // 1. 校验 manifest
        var manifest = PluginManifestReader.Read(pluginPath);
        if (!manifest.CoreVersion.IsCompatibleWith(typeof(IProvider).Assembly.GetName().Version!))
            throw new InvalidOperationException(
                $"Plugin '{manifest.ProviderName}' requires Core {manifest.CoreVersion}, " +
                $"current is {typeof(IProvider).Assembly.GetName().Version}");

        // 2. 创建 ALC
        var alc = new ProviderLoadContext(pluginPath, manifest.ProviderName);

        // 3. 加载程序集
        var asm = alc.LoadProviderAssembly(manifest.AssemblyPath);

        // 4. 通过特性找 Provider 实现
        var providerType = asm.GetTypes()
            .FirstOrDefault(t => t.GetCustomAttribute<ProviderAttribute>() is not null)
            ?? throw new InvalidOperationException(
                $"Plugin assembly '{asm.GetName().Name}' has no [Provider] type.");

        var provider = (IProvider)Activator.CreateInstance(providerType)!;

        return new LoadedPlugin(manifest, alc, provider);
    }

    private static bool IsSharedFramework(string? name)
        => name is not null
           && (name.StartsWith("System.", StringComparison.Ordinal)
               || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
               || name == "System"
               || name == "mscorlib"
               || name == "netstandard");
}
```

### 3. 卸载流程

```csharp
public async ValueTask UnloadAsync(string providerName)
{
    var plugin = _plugins[providerName];

    // 1. 从注册表移除（停止新调用）
    _providers.Unregister(providerName);

    // 2. 等待所有 in-flight 操作完成（带超时）
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await _operationTracker.WaitForProviderAsync(providerName, cts.Token);

    // 3. 调用 Provider 的 ShutdownAsync（如果实现）
    if (plugin.Provider is IAsyncDisposable asyncDisp)
        await asyncDisp.DisposeAsync();

    // 4. 卸载 ALC
    var weakRef = new WeakReference(plugin.ALC);
    plugin.ALC.Dispose();   // 触发 ALC.Unload()

    // 5. 等 GC 回收
    for (int i = 0; i < 30; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (!weakRef.IsAlive) break;
        await Task.Delay(100);
    }

    if (weakRef.IsAlive)
        _log.LogWarning("Provider '{Provider}' ALC was not collected after unload", providerName);

    _plugins.Remove(providerName);
}
```

### 4. Plugin Manifest

每个插件目录结构：

```
~/.openshell/providers/
└── my-provider/
    ├── MyProvider.dll
    ├── MyProvider.deps.json
    ├── plugin.json                  ← manifest
    └── deps/
        └── ...                      ← 私有依赖
```

`plugin.json`：

```json
{
  "providerName": "my-provider",
  "assemblyPath": "MyProvider.dll",
  "coreVersion": "0.1.0",
  "providerVersion": "1.0.0",
  "description": "My custom provider",
  "author": "..."
}
```

### 5. Provider 程序集特性

```csharp
[assembly: ProviderAssembly("my-provider", "1.0.0")]

[Provider("my-provider", Description = "...")]
public sealed class MyProvider : IProvider, IContainerProvider, ...
{
    ...
}
```

### 6. 内置 Provider 与第三方统一

M0 的 FileSystem/Archive/Registry/Remote 是内置 Provider：

- 编译时打包在主程序里
- 启动时通过同一 `PluginLoader` 加载，但用主 ALC（共享主程序依赖）
- 不支持卸载（与主程序同生命周期）

第三方 Provider 走独立 ALC，可卸载。

### 7. 依赖冲突检测

加载时检查 Provider 的依赖是否与已加载 Provider 冲突：

- 同一程序集名但版本不同 → 走各自 ALC（隔离成功）
- 同一程序集名且版本一致 → 默认共享，但若 Provider 显式声明 `privateDeps`，强制独立 ALC
- 加载失败抛 `PluginLoadException`，不影响其他 Provider

### 8. 开发模式

`~/.openshell/config.toml` 配置：

```toml
[plugins]
watch = true                # 监视 provider 目录变化，自动重载
hotReload = true            # 启用热重载
```

`FileSystemWatcher` 监视插件 DLL 变化，触发 `UnloadAsync` → 等待 1 秒 → `Load`。

## Alternatives Considered

1. **`Assembly.LoadFrom`**：被否决，无法卸载，依赖冲突无法解决
2. **MEF2 / VsComposition**：被否决，封装过厚，调试困难
3. **进程外插件（gRPC）**：被否决，序列化开销与跨进程序列化复杂度，M4+ 后才考虑
4. **`AssemblyLoadFile`**：被否决，与 `LoadFrom` 同样问题，且无依赖解析
5. **NativeAOT 兼容方案（静态注册）**：被否决，无法满足第三方运行时扩展

## Consequences

### 优势
- 第三方 Provider 独立依赖版本
- 热卸载 + 热重载
- 内置与第三方统一抽象
- 错误隔离（插件失败不影响主程序）

### 代价
- ALC 在 NativeAOT 不支持，需 fallback 静态注册
- 跨 ALC 边界对象必须严格是 `OpenShell.Core` 内接口/record
- 卸载 GC 等待时间不确定（< 3s 一般够）
- 调试堆栈跨 ALC 显示略复杂

### 约束
- `OpenShell.Core` 必须强命名或固定版本，否则不同 Provider 可能加载不同 Core 版本导致接口不兼容
- 跨 ALC 传递对象必须是 `interface` 或 `record`，禁止 `class` 实例
- Provider 程序集必须标记 `[assembly: ProviderAssembly(...)]`，否则拒绝加载
- `plugin.json` 缺失或字段不全的插件直接拒绝
- 卸载前必须等待所有 in-flight 操作完成（带超时），不允许在操作中卸载
- 卸载后必须显式触发 GC，等 `WeakReference.IsAlive == false`
- 热重载失败时必须保留旧 Provider 实例，避免运行时无 Provider
- 共享框架程序集（`System.*` / `Microsoft.Extensions.*`）走默认 ALC，禁止 Provider 私有副本
- Provider 实现禁止创建 `Thread` / `Timer` 等长生命周期非托管资源，必须实现 `IAsyncDisposable`
