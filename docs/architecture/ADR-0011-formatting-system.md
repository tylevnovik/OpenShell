# ADR-0011: 格式化系统

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M2
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0003 (Item 模型), ADR-0010 (Pipeline), ADR-0013 (GUI MVVM)

## Context

M2 同一对象流需要渲染成多种视图：

```
get-childitem | format-table name,size,modified      # CLI 表格
get-childitem | format-list                          # CLI 列表（一属性一行）
get-childitem | format-json                          # CLI JSON 输出
get-childitem | out-gridview                         # GUI 弹窗表格
get-childitem | out-file report.csv                  # 文件输出
get-childitem | out-default                          # 默认（CLI=table, GUI=list）
```

需求：

1. **CLI 与 GUI 共享 ViewSpec**：列定义、宽度、对齐、格式化函数在两端通用
2. **类型感知**：`Size` 字节自动转 KB/MB；`Modified` 时间按用户 locale；`Attributes` 枚举位域
3. **自定义列**：用户 `select name, ${size/1MB} as sizeMB` 计算列
4. **属性自动发现**：`format-table *` 列出所有属性
5. **流式渲染**：百万行的 `format-table` 不能等全部到达再渲染
6. **GUI 虚拟化**：`out-gridview` 必须用虚拟化列表，不全量加载
7. **导出**：CSV / JSON / Markdown 表格

PowerShell 的 Format-Table / Format-List / Out-GridView 是分离的命令，格式化与渲染耦合，导致自定义视图复杂。我们要更解耦。

## Decision

引入 **ViewSpec + IFormatter** 双层抽象：

### 1. ViewSpec（视图规范，不可变）

```csharp
public sealed record ViewSpec
{
    public required IReadOnlyList<ColumnSpec> Columns { get; init; }
    public ViewKind Kind { get; init; } = ViewKind.Table;
    public int? MaxRows { get; init; }             // 限制行数（CLI 默认 50，预览模式）
    public bool ShowHeader { get; init; } = true;
    public bool ShowFooter { get; init; } = true;   // "123 items"
}

public sealed record ColumnSpec
{
    public required string Name { get; init; }            // 属性名
    public string? DisplayLabel { get; init; }           // 列头，默认 = Name
    public int? Width { get; init; }                      // 列宽（CLI 字符，GUI 像素）
    public Alignment Align { get; init; } = Alignment.Left;
    public string? Format { get; init; }                  // 格式化字符串，如 "N0" / "yyyy-MM-dd"
    public Func<IItem, object?>? Projector { get; init; } // 自定义投影（计算列）
    public bool Wrap { get; init; } = false;
}

public enum ViewKind { Table, List, Json, Csv, Markdown }
public enum Alignment { Left, Right, Center }
```

### 2. IFormatter（渲染器）

```csharp
public interface IFormatter
{
    ViewKind SupportedKind { get; }

    /// <summary>流式渲染到 host。返回渲染的总行数。</summary>
    ValueTask<int> FormatAsync(
        IAsyncEnumerable<IItem> items,
        ViewSpec spec,
        IHost host,
        CancellationToken ct = default);
}
```

CLI 实现：
- `TableFormatter`：渲染 ASCII 表格，自动算列宽（基于前 10 行采样）
- `ListFormatter`：每行 `PropertyName: Value`
- `JsonFormatter`：JSON Lines（每行一个对象，便于 jq 解析）
- `CsvFormatter`：标准 CSV
- `MarkdownFormatter`：Markdown 表格

GUI 实现：
- `GridFormatter`：把 items 喂给 `ObservableCollection<IItem>` + DataGrid ViewModel（不是渲染文本，而是构建视图模型）
- `OutGridviewFormatter`：弹新窗口

### 3. ViewSpec 解析

`format-table name,size,modified` 的 Args：

```csharp
public record Args(
    [property: Parameter(Position = 0)] string[]? Properties = null,
    [property: Parameter(Aliases = new[]{"-a"})] bool AutoSize = false,
    [property: Parameter(Aliases = new[]{"-r"})] int? Rows = null,
    [property: Parameter] ViewKind As = ViewKind.Table);
```

Formatter 接收 Args 后构建 ViewSpec：
- `Properties` 为 null → 自动发现（取第一个 Item 的所有 Properties.Keys + 标准字段）
- `AutoSize` → 列宽自适应内容
- 显式 `name,${size/1MB} as sizeMB` → 自定义投影列（表达式由 ADR-0012 DSL 解析）

### 4. 类型感知格式化

注册 `IValueFormatter`：

```csharp
public interface IValueFormatter
{
    bool CanFormat(Type type);
    string Format(object? value, string? formatString);
}
```

默认注册：
- `FileSizeFormatter`：long → "1.2 KB" / "3.4 MB" / "1.0 GB"
- `DateTimeFormatter`：DateTimeOffset → 按 locale，默认 "yyyy-MM-dd HH:mm"
- `EnumFormatter`：枚举位域 → "ReadOnly, Hidden"
- `DefaultFormatter`：`ToString()` 兜底

Formatter 链按类型匹配，第一个命中胜出。

### 5. 流式渲染策略

`TableFormatter` 的工作流：

1. 接收 `IAsyncEnumerable<IItem>`
2. 取前 10 行采样，估算每列宽度
3. 打印表头
4. 流式打印每行，**遇到更宽的内容时调整列宽**（仅在前 100 行内调整，之后锁定）
5. 全部消费完后打印 footer（行数、总大小）

`MaxRows` 限制下，到上限后停止消费上游，发"truncated"提示。

### 6. GUI GridFormatter

不是渲染文本，而是把 `IAsyncEnumerable<IItem>` 喂给 `ObservableCollection<IItem>`，ViewSpec 转换为 Avalonia DataGrid 的 `DataGridColumnsCollection`。虚拟化由 Avalonia `VirtualizingStackPanel` 处理（见 ADR-0015）。

### 7. 默认 Sink 与隐式 Format

`Out-Default` 命令是默认 Sink，行为：
- CLI：调 `TableFormatter`，属性列表取 Item 标准 5 字段（Name/Kind/Size/Modified/Path）
- GUI：调 `GridFormatter`
- 用户可全局配置默认 ViewSpec（`~/.openshell/views/default.toml`）

### 8. 自定义视图文件

`~/.openshell/views/{TypeName}.toml` 定义特定 Item 类型的默认视图：

```toml
# views/fs-file.toml
kind = "Table"
columns = [
  { name = "Name", width = 40 },
  { name = "Size", format = "N0", align = "Right" },
  { name = "Modified", format = "yyyy-MM-dd HH:mm" },
]
```

Formatter 渲染时按 Item 的 `ContentType` / `Kind` 查找匹配视图文件。

## Alternatives Considered

1. **PowerShell 风格 Format-Table 内嵌渲染**：被否决，CLI 与 GUI 无法共享 ViewSpec
2. **直接 LINQ 投影到匿名对象后 ToString**：被否决，无类型感知、无自定义格式
3. **每 Item 类型注册视图（PSViewDefinition）**：被否决，过度分类，第三方 Provider 难维护
4. **JSON 中转（一切走 JSON）**：被否决，丢失类型信息，Size 格式化难
5. **DataGrid 直接绑定 IItem**：被否决，列定义无法控制，且 GUI 与 CLI 解耦失败

## Consequences

### 优势
- ViewSpec 双端复用
- 类型感知格式化（KB/MB、时间、枚举）
- 自定义计算列支持
- 流式渲染，百万行不 OOM
- GUI 虚拟化无缝衔接
- 自定义视图文件支持用户偏好

### 代价
- ViewSpec 解析与 DSL（ADR-0012）耦合，复杂度上升
- 流式表格的列宽调整有视觉抖动（前 100 行内）
- 自定义视图文件需文档说明 schema

### 约束
- ViewSpec 是 `sealed record`，不可变
- `IFormatter` 实现必须支持 `CancellationToken`
- `MaxRows` 达到后必须停止消费上游（不能仅截断显示）
- 默认 ViewSpec 必须可被命令参数覆盖，覆盖优先级：命令参数 > 自定义视图文件 > 内置默认
- `ColumnSpec.Projector` 不得有副作用
- GUI 的 GridFormatter 必须在 UI 线程更新 `ObservableCollection`，IO 在后台
- 自定义视图文件解析失败时降级到内置默认，不报错
- Formatter 实例必须无状态，可被多线程并发调用
