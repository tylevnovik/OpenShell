# ADR-0005: Provider 加载使用 AssemblyLoadContext，支持热卸载

- **Status**: Accepted
- **Date**: 2026-07-07
- **Decider**: Architecture
- **Supersedes**: —

## Context

OpenShell 是内部框架底座，预期被多个上层项目复用。Provider 是插件化的核心扩展点，需要满足：

- 第三方可在不修改 OpenShell 主程序的前提下开发 Provider（S3 兼容存储、企业内部 NAS、特定数据库等）
- Provider 程序集独立版本管理，避免与主程序的依赖冲突（如不同版本的 `AWSSDK.S3`）
- 开发期可"修改 Provider 代码 → 重新加载"而无需重启 host（开发体验）
- 远程更新场景下，Provider 可热替换不停服

`Assembly.Load` 加载的程序集无法卸载，依赖一旦冲突就锁死进程。PowerShell 的 PSModule 同样面临类似问题，但 PS 模块边界与程序集边界不严格对齐。

## Decision

每个 Provider 程序集加载到独立的 **`AssemblyLoadContext`（ALC）** 中：

```csharp
public sealed class ProviderLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public ProviderLoadContext(string pluginPath) : base(name: pluginPath, isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // 1. 让共享契约（OpenShell.Core）走默认 ALC，保证 IProvider 接口同一实例
        if (name.Name == "OpenShell.Core") return null;
        // 2. Provider 私有依赖走自己的目录解析
        return _resolver.ResolveAssemblyToStream(name) is { } asm ? LoadFromStream(asm) : null;
    }
}
```

约束：
1. **Provider 必须引用 `OpenShell.Core` 的NuGet 包**（不引用项目），保证 `IProvider` 接口在运行时与主程序同一实例
2. **`OpenShell.Core` 是 ALC 共享边界**：标记 `[AssemblyLoadContextAttribute]` 或在 `Load` 中显式 `return null` 让默认 ALC 处理
3. **isCollectible: true**：允许 `Unload()`，但要求 Provider 不持有非托管资源、不创建无法回收的线程
4. **卸载流程**：`IProviderRegistry.UnloadAsync(name)` → 等所有引用归零 → `ALC.Unload()` → 等待 GC 回收
5. **热卸载已实现 (M4+)**：`IPluginLoader.UnloadAsync` 完整流程已落地 (Per ADR-0016 §3), 包括等待 in-flight 操作归零、调用 Shutdown 钩子、卸载 ALC、GC 等待回收。MVP 阶段 (M0~M2) 仅做加载的约束已解除。

## Alternatives Considered

1. **`Assembly.LoadFile` 直接加载**：被否决，无法卸载、依赖冲突无法解决。
2. **MEF / MEF2**：被否决，MEF 的 DI 容器与主 DI 容器二选一，跨边界对象生命周期管理复杂；MEF 自身也基于 ALC，本质一样但封装更厚。
3. **进程外 Provider（gRPC 子进程）**：作为后续可选优化，首期不引入，避免跨进程序列化开销与调试复杂度。
4. **Source Generator + 静态注册**：被否决，第三方需重新编译主程序。

## Consequences

### 优势
- 第三方 Provider 独立依赖版本
- 开发期可重载，迭代快
- 远程更新不停服可行（M3+）

### 代价
- ALC 在 .NET 上需要框架支持，Mono / NativeAOT 兼容性需评估（首期仅支持 CoreCLR）
- 跨 ALC 边界的类型必须放在 `OpenShell.Core` 中，且严格只含接口/record，避免泄漏实现
- 调试时堆栈跨 ALC 略复杂，需在异常处理中标注来源程序集

### 约束
- `OpenShell.Core` 程序集必须强命名或版本固定，避免被解析为不同版本
- Provider 程序集必须带 `ProviderAssemblyAttribute`（自定义特性）声明 `ProviderName`、`ProviderVersion`，启动时校验
- 跨边界对象（`IItem` / `ItemPath` / `ProviderInfo`）必须是 `record` 或 `interface`，禁止 `class` 实例跨边界传递
- 首期 Provider（FileSystem/Archive/Registry/Remote）打包在主程序内置，通过同一 ALC 加载以简化首版实现；第三方 Provider 才走独立 ALC
