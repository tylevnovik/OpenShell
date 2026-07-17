# ADR-0015: 虚拟化列表与异步加载策略

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0002 (异步流式), ADR-0011 (格式化), ADR-0013 (MVVM)

## Context

GUI 列表需处理：

- **大目录**：用户主目录可能有 1w+ 文件，Windows 系统目录可达 10w+
- **远程 Provider**：S3 bucket 列举延迟 1-3s，全量加载用户体验差
- **缩略图**：图片预览生成耗时，不能阻塞列表渲染
- **过滤/排序**：用户输入"过滤框"后，需在不重置滚动的情况下更新列表
- **选中保持**：刷新目录后，原选中的文件应仍选中（如存在）
- **空目录/加载中/错误**：状态需明确显示
- **内存**：百万项不能全装入 ObservableCollection

需求约束：

- 首屏渲染 < 100ms（本地）/ < 500ms（远程缓存命中）
- 滚动 60fps
- 滚到可视区外 5 秒后释放缩略图
- 不破坏现有选中

Avalonia 的 `VirtualizingStackPanel` 支持基础虚拟化，但需要数据源配合。

## Decision

### 1. 数据源：分页 + 异步枚举

引入 `IItemsProvider<T>` 抽象，作为 `ItemsControl.ItemsSource`：

```csharp
public interface IItemsProvider<T>
{
    int Count { get; }                                  // 总数（未知返回 -1）
    ValueTask<T> GetItemAsync(int index, CancellationToken ct);
    ValueTask<IReadOnlyList<T>> GetRangeAsync(int start, int count, CancellationToken ct);
    event EventHandler<ItemsChangedEventArgs>? ItemsChanged;
}
```

实现 `AsyncItemsProvider<IItem>`，包装 `IAsyncEnumerable<IItem>`：

- 内部维护 `List<IItem?>` 缓存
- `GetRangeAsync(start, count)` 检查缓存命中，未命中部分调 `IAsyncEnumerable` 顺序读
- 后台预读下一页（默认 200 项）
- `Count` 在枚举完成前返回 -1，UI 显示"loading..."

### 2. 自定义 VirtualizingItemsControl

继承 Avalonia `ItemsControl`，重写 `ItemsPanel` 用 `VirtualizingStackPanel`：

- 创建自定义 `ItemsPresenter` 接 `IItemsProvider<IItem>`
- 可视区内 Item 调 `GetItemAsync`
- 滚动时批量预取
- 接到 `ItemsChanged` 通知时刷新可视区

### 3. 缩略图懒加载

`PaneView` 列表项左侧的 icon / 缩略图：

```csharp
public sealed class ThumbnailViewModel : ReactiveObject
{
    private ObservableValue<IBitmap?> _thumbnail = ObservableValue<IBitmap?>(null);
    public IObservable<IBitmap?> Thumbnail => _thumbnail;

    public async Task LoadAsync(IItem item, IThumbnailService svc, CancellationToken ct)
    {
        var bmp = await svc.GetThumbnailAsync(item, size: 32, ct);
        _thumbnail.Value = bmp;
    }
}
```

- 列表项模板绑定 `Thumbnail`
- 进入可视区时触发 `LoadAsync`，离开 5 秒后 dispose 释放位图
- `IThumbnailService` 内部 LRU 缓存（1000 张），按 ItemPath+Modified 缓存

### 4. 加载状态机

PaneViewModel 状态：

```
Loading → Loaded
   │        │
   ↓        ↓
Error   Refreshing → Loaded
```

- `IsLoading` bool 控制 spinner 显示
- `ErrorMessage` string 控制 error panel 显示
- `IsEmpty` bool 控制 "directory is empty" 提示

### 5. 过滤与排序的虚拟化策略

| 操作 | 实现 | 性能 |
|---|---|---|
| **简单过滤**（属性 = 值） | 直接调 Provider 的 `EnumerationOptions.Filter`，让 Provider 端做 | O(n) Provider 内 |
| **复杂过滤**（DSL 表达式） | 加载全部到缓存后用 `IPipelineTransform` 过滤 | O(n) 内存 |
| **本地排序**（已加载全部） | 用 `sort` Pipeline 节点 | O(n log n) |
| **远程排序** | Provider 端枚举时排序（如 S3 按修改时间） | Provider 内 |

策略：

- 用户在"过滤框"输入时，500ms 防抖后触发
- 优先用 Provider 端 `Filter` 选项
- DSL 表达式无法下沉到 Provider 时退回本地过滤
- 排序默认本地，远程大数据集需用户手动选 "load all"

### 6. 选中保持

刷新目录前记录 `SelectedItems` 的 `ItemPath`，刷新后按 Path 匹配重新选中：

```csharp
var prevSelectedPaths = SelectedItems.Select(i => i.Path).ToHashSet();
// refresh...
Items.Cast<IItem>().Where(i => prevSelectedPaths.Contains(i.Path))
    .ForEach(i => selection.Add(i));
```

### 7. 错误处理

- `UnauthorizedAccessException`：列表中显示该项但置灰，点击弹出"无权限"
- 远程超时：显示重试按钮
- 加载中失败：保留已加载项，错误提示在状态栏

### 8. 性能预算

| 场景 | 目标 |
|---|---|
| 本地 1000 项首屏 | < 100ms |
| 本地 10w 项首屏 | < 200ms（只读可视区） |
| 远程缓存命中 | < 300ms |
| 远程缓存未命中 | < 3s（显示 spinner） |
| 滚动 60fps | 单项渲染 < 16ms |
| 缩略图加载 | 后台，不阻塞滚动 |
| 内存占用（10w 项） | < 50MB |

## Alternatives Considered

1. **一次性加载所有项**：被否决，OOM 风险，远程超时
2. **DataGrid 内置虚拟化**：被否决，DataGrid 不支持 `IAsyncEnumerable` 数据源，需自己包装
3. **每次滚动都重新查 Provider**：被否决，远程延迟不可接受
4. **全部缓存到内存**：被否决，10w+ 文件占内存大
5. **用 `INotifyCollectionChanged` 同步加项**：被否决，大量 add 通知 UI 卡顿
6. **Avalonia `ItemsRepeater`**：被否决，更轻量但不支持 `IItemsProvider` 抽象，需自己实现虚拟化协调

## Consequences

### 优势
- 大目录响应快
- 远程可接受延迟
- 缩略图懒加载
- 过滤/排序下沉到 Provider
- 选中保持

### 代价
- 自定义 ItemsControl 维护成本
- 状态机复杂（Loading / Loaded / Error / Empty）
- 缩略图 LRU 调试麻烦

### 约束
- `IItemsProvider<T>` 必须线程安全（读后台线程，渲染 UI 线程）
- `GetItemAsync` 必须支持 cancellation
- `Count = -1` 时 UI 必须显示"loading..."而非 0
- 缩略图大小固定（32x32），不同尺寸需独立缓存
- LRU 缓存驱逐策略：按访问时间，最近最少使用驱逐
- 选中保持必须按 `ItemPath` 等值匹配，不能按引用
- `ItemsChanged` 通知必须包含 `NotifyCollectionChangedAction.Reset`，禁止逐项通知
- 缩略图 dispose 必须在 UI 线程，否则 Avalonia 位图跨线程访问崩溃
- 远程超时默认 30s，可配置
