# ADR-0001: Provider 能力模型采用接口组合

- **Status**: Accepted
- **Date**: 2026-07-07
- **Decider**: Architecture
- **Supersedes**: —

## Context

OpenShell 需要在 CLI 与 GUI 两个 host 上复用同一套 Provider 抽象，覆盖 FileSystem / Archive / Registry / Remote 共 4 类数据源。它们的能力差异很大：

- FileSystem：读 + 写 + Content + Property + Security + Drive
- Archive (zip/tar)：读 + Content + Property，无 Security、无原生写
- Registry：读 + 写 + Property + Security，无 Content（值以 Property 形式存在）
- Remote S3：读 + 可选写 + Property，无 Security

PowerShell 的 `ProviderBase` + `CmdletProvider` 是单一大接口 + 大量 `virtual` 抛 `NotSupportedException`，导致每个 Provider 都要写一坨"不支持"的样板代码，且能力探测只能在运行时通过 `throw` 反射判断，命令分发层无法在调度前拒绝不合法的调用。

## Decision

采用**接口组合**而非单大接口：

```csharp
public interface IProvider
{
    ProviderInfo Info { get; }
    IReadOnlySet<ProviderCapability> Capabilities { get; }
}

public interface IItemProvider { ... }
public interface IContainerProvider { ... }
public interface INavigationProvider { ... }
public interface IContentProvider { ... }
public interface IPropertyProvider { ... }
public interface ISecurityProvider { ... }
public interface IDriveProvider { ... }

public enum ProviderCapability { Item, Container, Navigation, Content, ContentWrite, Property, Security, Drive }
```

每个 Provider 按需实现，`Capabilities` 集合作为强声明。

## Alternatives Considered

1. **PowerShell 风格单接口 + 抛异常**：被否决，运行时错误、命令分发无法预检、Provider 代码冗余。
2. **特性标注 + 反射**（`[Supports(Portability.Content)]`）：被否决，特性无编译期约束，且不能把方法签名分到不同接口。
3. **Visitor 模式**：被否决，增加新能力时要改 visitor 接口，违反开闭原则。

## Consequences

### 优势
- 命令分发层可在调度前通过 `Capabilities` / `is IContainerProvider` 双重判断拒绝不合法调用
- Provider 只实现真正支持的能力，无样板代码
- 新能力可独立扩展，不破坏既有 Provider
- 单元测试可针对单个能力接口 mock

### 代价
- Provider 类可能实现多个接口，类型转换略繁（可由 `CommandContext.ResolveProvider<T>` 封装）
- DI 注册需扫描所有接口实现

### 约束
- `Capabilities` 集合必须与实际实现的接口一致，由单元测试守卫（`ProviderContractTests`）
- 不允许"声明能力但不实现接口"的情况
