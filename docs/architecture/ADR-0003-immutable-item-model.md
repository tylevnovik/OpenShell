# ADR-0003: Item 是不可变 record，修改操作返回新实例

- **Status**: Accepted
- **Date**: 2026-07-07
- **Decider**: Architecture
- **Supersedes**: —

## Context

`IItem` 是 Provider 暴露给上层的核心数据载体，会被以下场景同时消费：

- CLI 渲染：把 Item 转成表格行
- GUI 虚拟化列表：绑定到 `ObservableCollection<IItem>`，需要 `INotifyPropertyChanged` 或不可变快照
- Pipeline 节点：filter / sort / project 可能基于 Item 派生新对象
- 缓存层：缓存命中需保证不被后续操作修改
- 并发枚举：一个 `IAsyncEnumerable<IItem>` 可能被多个订阅者消费

若 `IItem` 是可变对象（属性 setter），上述场景会出问题：缓存被污染、GUI 绑定闪烁、并发竞态。

## Decision

`IItem` 设计为**不可变 `record`**：

```csharp
public sealed record Item : IItem
{
    public required ItemPath Path { get; init; }
    public required ItemKind Kind { get; init; }       // File | Directory | SymbolicLink | ...
    public required ItemTimestamps Timestamps { get; init; }
    public long? Size { get; init; }
    public PropertyBag Properties { get; init; } = PropertyBag.Empty;
    public string? ContentType { get; init; }
    // 无 setter，无 mutable 字段
}
```

所有"修改"操作（rename / set-property / touch 等）**返回新 `Item` 实例**，原实例不变。

## Alternatives Considered

1. **可变 `class` + `INotifyPropertyChanged`**：被否决，跨线程难、缓存污染、并发不安全。
2. **可变 `struct`**：被否决，装箱语义混乱、字段拷贝陷阱。
3. **`ImmutableXXX` 风格的 With 模式而非 `record`**：`record` 已提供 `with` 表达式，语义更直接。

## Consequences

### 优势
- 天然线程安全，可自由跨线程传递、缓存、订阅
- GUI 绑定可基于"替换整个 Item"而非"通知属性变更"，简化虚拟化逻辑
- Pipeline 中间节点无需深拷贝
- 单元测试断言可基于值相等（`record` 自带 `Equals`）

### 代价
- 频繁修改场景（如批量改属性）会多次分配，但 `with` 表达式已用 `MemberwiseClone` 优化
- GUI 中"实时更新"需用 `IObservable<IItem>` 替代属性通知，由 Bridge 层封装

### 约束
- `IItem` 实现必须是 `record`，禁止 `class` 或带 `set` 的实现，由 Roslyn analyzer 守卫
- `PropertyBag` 内部用 `ImmutableDictionary<string, object>`，不允许暴露 `IDictionary`
- 大对象修改（如读 `Content`）不通过 `Item` 携带，而通过 `IContentProvider.OpenReadAsync` 单独获取 `Stream`
