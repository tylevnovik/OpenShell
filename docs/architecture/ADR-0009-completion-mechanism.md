# ADR-0009: 命令补全机制

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0004 (命令系统), ADR-0008 (REPL), ADR-0006 (路径)

## Context

Tab 补全是 CLI 体验的核心。M1 的补全场景包括：

| 输入位置 | 期望补全 |
|---|---|
| `ls \| > c` | `cd`, `clear`, `copy-item` 等命令名 |
| `get-childitem -` | 参数名 `-Path` `-Filter` `-Recurse` |
| `get-childitem -Path ` | 当前 `CurrentLocation` 下的路径 |
| `cd fs::C:/Users/` | 该路径下的子目录 |
| `get-childitem -Filter ` | glob（如 `*.txt`） |
| `set-itemproperty -Type ` | enum 值（`String`/`DWord`/`QWord`） |
| `copy-item ` ` ` `archive.zip` | 已完成的命令参数（前一个参数的值） |
| `get-childitem \| s` | Pipeline 节点 `select`/`sort` |
| 空输入 | 历史中最近 5 条 |

需求约束：
- 响应延迟 < 50ms（远程 Provider 用缓存）
- 单 Tab 列出所有候选，双 Tab 自动补全到公共前缀
- 路径补全需感知 Provider（`zip::`、`reg::` 等前缀也补全）
- 补全项可能有附加信息（描述、图标），不只在 CLI 用，GUI 的命令面板也复用

PowerShell 的补全是基于 `CommandCompletion` + TabExpansion2，机制成熟但实现深耦合 PS 的 AST 与 Host。我们需要自研轻量版本。

## Decision

采用**多源组合 + 上下文感知**的补全机制：

### 1. 接口契约

```csharp
public readonly record struct CompletionItem(
    string Label,                          // 显示文本
    string InsertText,                    // 实际插入文本（可能与 Label 不同，如带引号）
    CompletionKind Kind,                  // Command/Parameter/Path/Glob/Enum/Variable
    string? Description,                   // 一行描述
    string? Documentation,                 // 多行文档（GUI 用）
    bool HasTrailingSpace = true);

public enum CompletionKind { Command, Parameter, Path, Glob, Enum, Variable, Keyword }

public interface ICompletionSource
{
    /// <summary>返回按优先级排序的补全项。cursorPosition 是输入文本中的光标位置。</summary>
    ValueTask<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        CompletionContext context, CancellationToken ct = default);
}

public sealed record CompletionContext(
    string FullInput,
    int CursorPosition,
    CommandDescriptor? CurrentCommand,    // 已解析的命令（null 表示在命令名阶段）
    int CurrentParameterPosition,         // 当前参数位置（0=命令名, 1=第一个位置参数, ...）
    ParameterDescriptor? CurrentParameter,// 当前所在的参数（null 表示未知）
    ItemPath CurrentLocation);
```

### 2. 多源链式策略

补全调度器（`CompletionAggregator`）按光标位置选源：

| 位置 | 源 | 说明 |
|---|---|---|
| 命令名（第 0 个 token） | `CommandNameCompletionSource` | 从 `ICommandRegistry` 取所有 `FullName` + `Aliases` |
| `-` 开头 token | `ParameterNameCompletionSource` | 从 `CurrentCommand.Parameters` 取 |
| `-Path`/`-Destination` 等已知路径参数 | `PathCompletionSource` | 调 `IContainerProvider.GetChildrenAsync` |
| `-Filter`/`-Include`/`-Exclude` | `GlobCompletionSource` | 列出当前目录文件名作为 glob 候选 |
| enum 类型参数 | `EnumCompletionSource` | 反射 enum 字段 |
| Pipeline 后（`\|` 后） | `PipelineNodeCompletionSource` | `where`/`select`/`sort`/`format-*`/`out-*` |
| 空输入 | `HistoryCompletionSource` | 最近 5 条历史 |
| 兜底 | `PathCompletionSource` | 默认按路径处理 |

每个源独立实现 `ICompletionSource`，调度器按位置调用对应源。

### 3. 路径补全细节

```csharp
public sealed class PathCompletionSource : ICompletionSource
{
    public async ValueTask<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        CompletionContext ctx, CancellationToken ct)
    {
        // 1. 解析光标前的 token，分离 provider:: 前缀
        var (provider, partialPath) = ParsePrefix(tokenBeforeCursor);

        // 2. 决定枚举位置：partialPath 的父目录
        var parentPath = new ItemPath { Provider = provider, InternalPath = GetParent(partialPath) };
        var namePrefix = GetName(partialPath);

        // 3. 远程 Provider 用缓存（5s TTL）
        var container = _providers.ResolveCapability<IContainerProvider>(parentPath);
        if (container is null) return Array.Empty<CompletionItem>();

        // 4. 流式枚举 + 前缀过滤，取前 100 项避免过载
        var results = new List<CompletionItem>();
        await foreach (var item in container.GetChildrenAsync(parentPath, opts, ct))
        {
            if (!item.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(ToCompletionItem(item));
            if (results.Count >= 100) break;
        }
        return results;
    }
}
```

### 4. 远程 Provider 缓存

`PathCompletionSource` 内部维护 `ConcurrentDictionary<ItemPath, (DateTime, List<IItem>)>`：

- key 是枚举的父目录路径
- TTL 默认 5 秒，可按 Provider 配置（Remote 30s，FileSystem 0s）
- 任何写操作（cp/mv/rm）通过 `IProviderRegistry` 的事件总线失效相关缓存（M3 引入事件总线，M1 先 TTL 失效）
- 缓存仅用于补全，不影响命令执行

### 5. Provider 前缀补全

`zip::archive.zip/` 这种前缀也需 Tab 补全：

- 光标在 `z` → 列出所有注册的 Provider 名（`zip`/`reg`/`s3`/`fs`）
- 光标在 `zip::` 之后 → 切换到 Archive Provider 的路径补全
- 调度器识别 `::` 分隔符决定走哪个 Provider

### 6. GUI 复用

`ICompletionSource` 与 CLI 无关，GUI 的"命令面板"（Ctrl+Shift+P 风格）也调同一接口，渲染方式不同（弹窗列表而非终端替换）。

## Alternatives Considered

1. **静态补全表**（命令 + 参数名固定）：被否决，不能反映运行时 Provider 状态，路径补全失效。
2. **Parser 完整解析后补全**：被否决，部分输入常常不完整，Parser 报错就无法补全；需用容错解析器。
3. **基于 AST 的补全**（如 PowerShell）：被否决，M1 的 DSL 还没到需要 AST 的复杂度；M2 加 Pipeline 后再考虑。
4. **每命令自定义补全逻辑**：被否决，命令作者负担重；通用源 + 特殊参数标签覆盖 90% 场景。
5. **远程补全实时调 API**：被否决，延迟无法保证；缓存是必需的。

## Consequences

### 优势
- 多源组合覆盖所有补全场景
- 远程 Provider 缓存保证响应延迟
- GUI 复用同一接口
- 命令作者无需关心补全（除非加自定义源）

### 代价
- 调度器需要正确的"位置判断"逻辑，部分输入（如 `get-childitem -` 后光标移回命令名）需特殊处理
- 远程缓存可能短暂过时，用户需手动刷新（`rehash` 命令）
- 路径补全依赖 `IContainerProvider`，注册表类 Provider 没有"目录"概念，需特殊处理

### 约束
- 补全响应必须 < 50ms（缓存命中）/ < 200ms（缓存未命中但本地）/ 异步可超时（远程）
- 补全项数量上限 100 项，超出时按字典序截断 + 提示
- 路径补全的 `InsertText` 必须处理含空格的路径（自动加引号）
- `CompletionItem` 是 `record`，不可变
- 补全源不得有副作用（不修改任何状态）
- 远程缓存失效策略必须可扩展（M3 的事件总线接入后切换到事件驱动）
- Provider 前缀补全（`zip::` 等）必须由调度器统一处理，不交由各 PathCompletionSource
