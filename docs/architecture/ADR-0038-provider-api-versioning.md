# ADR-0038: Provider API 版本与废弃策略

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: Cross-cutting
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0001 (Provider 接口), ADR-0005 (ALC 加载), ADR-0016 (ProviderLoadContext), ADR-0023 (命令版本演进), ADR-0037 (自动更新)

## Context

ADR-0001 定义了 7 个能力接口（`IProvider`/`IItemProvider`/`IContainerProvider`/`INavigationProvider`/`IContentProvider`/`IPropertyProvider`/`ISecurityProvider`/`IDriveProvider`），ADR-0016 定义了 ALC 加载机制，但均未给出：

- Core 侧接口版本号如何标注（`IItemProvider` 改了签名怎么办）
- Provider 声明自身依赖的 Core 版本范围
- 旧 Provider 加载到新 Core 上的兼容性矩阵
- 接口废弃流程（`Obsolete` 特性、警告、移除时间表）
- 跨大版本迁移路径（v1 → v2 接口重写）
- 运行时检测：Provider 加载时如何检查 API 兼容性、不兼容如何降级或拒绝

无规范会导致：第三方 Provider 写完编译后再也不更新、Core 改接口后旧 Provider 静默崩、用户不知道某个 API 已废弃、升级 Core 后整个生态断档。

## Decision

### 1. 版本号体系（SemVer 2.0）

- **Core 包版本**：`OpenShell.Core` 跟随 OpenShell 主版本，独立 SemVer 2.0
- **Provider API 版本**：与 Core 包版本解耦，单独的 `ProviderApiVersion`，形如 `1.0.0`、`2.0.0-beta.1`
- **Provider 声明**：

```csharp
public sealed class ProviderInfo
{
    public string Name { get; init; }                    // "fs", "zip"
    public Version Version { get; init; }                // Provider 实现版本，如 1.2.0
    public Version RequiredApiVersion { get; init; }     // 依赖的 ProviderApiVersion，如 1.0.0
    public ProviderApiStability ApiStability { get; init; } = ProviderApiStability.Stable;
}

public enum ProviderApiStability { Stable, Preview, Experimental }
```

- `RequiredApiVersion` 的主版本号必须等于当前 Core 的 `ProviderApiVersion` 主版本号，否则视为不兼容
- 次版本/修订版本号差异视为向前兼容（Provider 可声明 `>= 1.2.0`，Core 是 `1.5.0` 也接受）

### 2. 兼容性矩阵

| Core ProviderApiVersion | Provider RequiredApiVersion | 行为 |
|---|---|---|
| 1.x.y | 1.x.y' （y' ≤ y） | 完全兼容 |
| 1.x.y | 1.x'.y' （x' > x） | 兼容（Provider 用了新 API，Core 提供了） |
| 1.x.y | 2.x.y | **不兼容**，加载时拒绝并提示升级 |
| 2.x.y | 1.x.y | **不兼容**，加载时拒绝并提示降级或迁移 |

加载时检查由 `ProviderLoadContext`（ADR-0016）执行：

```csharp
public sealed class ApiMismatchException : Exception
{
    public required ProviderInfo ProviderInfo { get; init; }
    public required Version HostApiVersion { get; init; }
    public required Version RequiredApiVersion { get; init; }
    public required string Remediation { get; init; }   // "升级 OpenShell 到 >= X.Y" 或 "联系 provider 作者"
}
```

### 3. 接口废弃流程

接口或其成员废弃分三个阶段：

| 阶段 | 时长 | 行为 |
|---|---|---|
| **Preview** | 1 个 milestone | 新接口发布，旧接口保持原样 |
| **Deprecated** | 2 个 milestone | 旧接口加 `[Obsolete("Use INewInterface instead, will be removed in v3")]`，加载使用旧接口的 Provider 时输出 WARNING 日志 |
| **Removed** | 主版本提升 | 旧接口删除，Provider 编译失败必须迁移 |

特性定义：

```csharp
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Property)]
public sealed class ProviderApiAttribute : Attribute
{
    public Version? SinceVersion { get; init; }       // 引入版本
    public Version? DeprecatedSince { get; init; }    // 废弃起始版本
    public Version? RemovedIn { get; init; }          // 移除版本
    public string? Replacement { get; init; }         // 替代 API 全名
    public string? MigrationNotes { get; init; }      // 迁移说明
}
```

示例：

```csharp
[ProviderApi(SinceVersion = "1.0.0")]
public interface IItemProvider { ... }

// v2 引入更高效的批量接口
[ProviderApi(SinceVersion = "2.0.0")]
public interface IBatchItemProvider
{
    IAsyncEnumerable<IItem> GetItemsAsync(IReadOnlyList<ItemPath> paths, CancellationToken ct = default);
}

// v2 废弃旧的单项接口
[ProviderApi(SinceVersion = "1.0.0", DeprecatedSince = "2.0.0", RemovedIn = "3.0.0", Replacement = "IBatchItemProvider")]
public interface IItemProvider { ... }
```

### 4. Provider 元数据清单

每个 Provider 包根目录必须提供 `openshell.provider.json`：

```json
{
  "name": "OpenShell.Providers.S3",
  "version": "1.2.0",
  "requiredApiVersion": "1.0.0",
  "apiStability": "Stable",
  "authors": ["..."],
  "repository": "https://github.com/.../openshell-s3",
  "license": "MIT",
  "description": "S3 remote provider with multipart upload",
  "capabilities": ["Item", "Container", "Navigation", "Content", "Property", "Drive"],
  "dependencies": [
    { "name": "OpenShell.Providers.Remote", "version": ">= 1.0.0" }
  ],
  "minimumHostVersion": "1.0.0"
}
```

`ProviderLoadContext` 加载时优先读此清单，再做 API 兼容性检查。

### 5. 主版本迁移指南

每主版本提升必须提供 `docs/migration/vN-to-v(N+1).md`，内容包括：

- 移除的 API 全列表 + 替代方案
- 行为变化（如 `GetChildrenAsync` 默认行为变更）
- 迁移示例代码（before / after）
- 自动迁移工具（如有）：`dotnet openshell migrate --api v1-to-v2 <provider.dll>`

### 6. 稳定性分级

| 级别 | 含义 | 兼容性承诺 |
|---|---|---|
| `Stable` | GA 接口 | 主版本内不破坏 |
| `Preview` | 预览接口 | 次版本可能破坏，加 `[Preview]` 特性 |
| `Experimental` | 实验性 | 任意版本可能破坏，默认不加载，需 `--enable-experimental` |

CLI 显示：

```
> Get-Provider
Name    Version  ApiVersion  Stability      Capabilities
fs      1.0.0    1.0.0       Stable         Item,Container,...
s3      0.9.0    1.0.0       Preview        Item,Container,...
exp     0.1.0    1.0.0       Experimental   Item  (loaded via --enable-experimental)
```

## Alternatives Considered

1. **不版本化接口，依赖编译期检查**：被否决，运行时反射加载无法保证
2. **每个接口独立版本号**：被否决，组合爆炸、Provider 实现负担过重
3. **严格 SemVer，无 Preview/Experimental 分级**：被否决，无法承载快速迭代需求
4. **接口变更直接破坏，不提供废弃期**：被否决，第三方生态无法承受
5. **用 .NET AssemblyVersion 强校验**：被否决，与 ALC 卸载冲突，且无法表达兼容性范围

## Consequences

### 优势
- 第三方 Provider 有明确的兼容性契约
- 废弃流程透明，用户可预期迁移时间表
- Preview/Experimental 通道支持快速试验
- Core 团队有清晰的版本演进路径

### 代价
- Core 接口变更评审成本上升（要写迁移指南）
- Provider 作者需维护 `openshell.provider.json` 清单
- 加载时多一次版本检查开销（可忽略）
- 主版本提升涉及全生态协调

### 约束
- Provider 必须声明 `RequiredApiVersion`，否则视为 Experimental 拒绝加载
- 主版本提升必须同步发布迁移指南
- 废弃接口的 Deprecated 阶段至少 2 个 milestone
- Experimental 接口默认不进入 Stable 命令清单（ADR-0023）
- `openshell.provider.json` 必须签名（与 ADR-0036 安全沙箱协同）
- 兼容性矩阵表必须维护在 `docs/migration/api-compatibility.md`
