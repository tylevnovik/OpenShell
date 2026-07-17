# ADR-0002: 所有 IO API 必须 async + CancellationToken + IAsyncEnumerable/Stream

- **Status**: Accepted
- **Date**: 2026-07-07
- **Decider**: Architecture
- **Supersedes**: —

## Context

OpenShell 首期就要支持 Remote / 云存储 Provider（S3/WebDAV/SSH），同时双端并行（CLI 与 GUI 共用 Core）。同步阻塞 API 在以下场景会出问题：

- 远程 Provider 网络抖动时阻塞调用线程，CLI 卡死无 Ctrl+C 响应、GUI 主线程冻结
- 大目录枚举（百万文件）必须流式返回，否则一次性 `List<T>` 会 OOM
- GUI 需要在枚举过程中实时插入虚拟化列表，必须能拿到 `IAsyncEnumerable`
- CLI 渲染也需流式输出，逐行打印而非等全部完成

## Decision

Core 层所有 IO 类 API 必须满足：

1. **返回类型**：单值用 `ValueTask<T>` / `ValueTask<T?>`，序列用 `IAsyncEnumerable<T>`，二进制用 `Stream`（异步读取）
2. **签名末尾必有 `CancellationToken`**：默认值 `default`，调用方可传 `ct`
3. **禁止同步重载**：不提供 `T GetItem(ItemPath)` 这类同步便捷方法
4. **禁止 `IEnumerable<T>`**：所有枚举必须是 `IAsyncEnumerable<T>`

示例：

```csharp
public interface IContainerProvider
{
    IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        CancellationToken cancellationToken = default);
}

public interface IContentProvider
{
    ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default);
}
```

## Alternatives Considered

1. **同步 + 异步双 API**（如 BCL 风格）：被否决，双套实现、双套测试、Provider 作者负担大。
2. **`Task<T>` 而非 `ValueTask<T>`**：被否决，高频同步完成路径（如缓存命中）会分配不必要的 Task 对象。
3. **`IObservable<T>` 替代 `IAsyncEnumerable<T>`**：仅在 Bridge 层（GUI 绑定）使用，Core 内部仍用 `IAsyncEnumerable`，由 Bridge 转换。

## Consequences

### 优势
- 远程 Provider 天然异步，无阻塞
- 大目录流式枚举，内存可控
- Ctrl+C / 取消按钮统一通过 `CancellationToken` 传播
- GUI 虚拟化列表可在数据到达时增量更新

### 代价
- Provider 作者必须熟悉 async/await，不能写同步代码
- 测试需要 `async Task` 测试方法，需处理同步上下文

### 约束
- Core 内禁止 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`，由 Roslyn analyzer（`AsyncSuffixAnalyzer` 或自定义）守卫
- `CancellationToken` 必须是最后一个参数，必须有默认值 `default`
- Bridge 层负责把 `IAsyncEnumerable<T>` 转 `IObservable<T>` 给 GUI，Core 不依赖任何 Reactive 框架
