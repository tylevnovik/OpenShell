# ADR-0008: CLI REPL 架构

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0004 (命令系统), ADR-0009 (补全)

## Context

M0 的 CLI 是 `Console.ReadLine` + `Console.WriteLine` 的最简循环，没有补全、历史、ANSI 渲染、多行输入、Ctrl+C 优雅退出。M1 需要做成接近 PowerShell / nushell 体验的 REPL：

- Tab 补全（命令名 / 参数名 / 路径 / glob / enum）
- 命令历史（↑↓ 翻阅，跨会话持久化）
- 多行输入（管道 `\` 续行、未闭合引号提示）
- ANSI 颜色渲染（错误红、目录蓝、提示符绿）
- Ctrl+C 中断当前命令但保留 REPL
- 异步执行：长命令运行时 UI 仍可输入下一条（M3+ 才上，M1 单线程够用）
- 跨平台：Windows Terminal / ConPTY、Linux xterm、macOS Terminal.app

可选项：
- **PrettyPrompt**：C# 库，成熟，支持补全/历史/ANSI，但 API 略重
- **reedline**（Rust）：不能直接用
- 自己从零写：跨平台终端处理太复杂（VT100 转义、Unicode 宽字符、信号）
- **System.Console** + readline 风格：功能太弱

## Decision

采用**分层 REPL 架构**，每层接口可替换：

```
┌─────────────────────────────────────────────────────┐
│ ReplEngine (顶层循环)                                │
│  - 读取输入 / 解析 / 调度 / 渲染输出                  │
│  - Ctrl+C 处理                                       │
└───────┬──────────────────────────────────────┬──────┘
        │                                      │
┌───────▼─────────┐                  ┌─────────▼──────────┐
│ ILineEditor     │                  │ IOutputRenderer    │
│ (行编辑)         │                  │ (输出渲染)          │
├─────────────────┤                  ├────────────────────┤
│ ReadLineAsync() │                  │ RenderItems(...)   │
│ Prompt          │                  │ RenderError(...)   │
│ Multi-line      │                  │ RenderProgress(...)│
└───────┬─────────┘                  └─────────┬──────────┘
        │                                      │
┌───────▼─────────┐                  ┌─────────▼──────────┐
│ ICompletionSrc  │                  │ IAnsiSequences    │
│ (补全数据源)     │                  │ (终端能力)         │
└─────────────────┘                  └────────────────────┘
```

### 接口契约

```csharp
public interface ILineEditor
{
    (string Text, LineEditResult Result) ReadLine(Prompt prompt, CancellationToken ct = default);
    ICompletionSource CompletionSource { get; set; }
    IHistory History { get; set; }
}

public interface ICompletionSource
{
    ValueTask<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        string input, int cursor, CommandContext? ctx, CancellationToken ct);
}

public interface IHistory
{
    IReadOnlyList<string> Entries { get; }
    void Append(string line);
    void Save(string path);
    void Load(string path);
}

public interface IOutputRenderer
{
    Task RenderItemsAsync(IAsyncEnumerable<IItem> items, ViewSpec? spec, CancellationToken ct);
    void RenderError(string message, Exception? ex = null);
    void RenderProgress(OperationProgress progress);
}

public interface IAnsiSequences
{
    bool SupportsColor { get; }
    string Color(AnsiColor color, string text);
    string Bold(string text);
    string Dim(string text);
}
```

### 默认实现选型

- **ILineEditor**：M1 默认实现基于 `PrettyPrompt`（NuGet 包 `PrettyPrompt` 4.0+）。它提供 Tab 补全、历史、多行、ANSI、Ctrl+C，API 是 `Prompt.ReadAsync()`，可适配我们的接口。后续如需更细控可换自研。
- **ICompletionSource**：自研，组合多个子 Source（见 ADR-0009）。
- **IHistory**：自研，JSON 持久化到 `~/.openshell/history.jsonl`，单条记录含命令文本、时间戳、退出码。
- **IOutputRenderer**：自研，CLI 实现 `ConsoleOutputRenderer`。
- **IAnsiSequences**：自研 `AnsiSequences`，启动时探测 `Console.IsOutputRedirected`、`NO_COLOR` 环境变量、`TERM` 环境变量决定能力降级。

### ReplEngine 主循环（伪代码）

```csharp
public async Task RunAsync(CancellationToken externalCt)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
    while (!cts.IsCancellationRequested)
    {
        var prompt = BuildPrompt();   // 含 CurrentLocation.Display
        var (line, result) = _lineEditor.ReadLine(prompt, cts.Token);
        if (result == LineEditResult.Cancelled) continue;
        if (result == LineEditResult.Exit) break;

        try
        {
            var exitCode = await DispatchAsync(line, cts.Token);
            _history.Append(line, exitCode);
        }
        catch (OperationCanceledException) { /* Ctrl+C */ }
        catch (Exception ex) { _renderer.RenderError("error", ex); }
    }
}
```

### Ctrl+C 处理

- `Console.CancelKeyPress`：先尝试 `cts.Cancel()` 优雅停止；若用户在 2s 内再次按 Ctrl+C，则强制退出 REPL
- 正在执行的命令收到 `OperationCanceledException`，自行清理（如部分复制保留）
- 行编辑期间的 Ctrl+C 由 `PrettyPrompt` 处理，返回 `LineEditResult.Cancelled`

### 多行输入

- 续行触发器集合：`{`, `(`, `[`, `"`, `'`, here-string (`@"` / `@'`), `\`, `|`
- 行尾 `\` 触发续行，Prompt 变成 `... `
- 未闭合的 `"`, `'`, `|`, `(`, `[`, `{` 也触发续行
- here-string（`@"` / `@'`）未闭合时持续续行
- 多行整体作为一条命令送 Parser
- 完整的续行触发器集合由 `InputCompletenessChecker` 实现（per ADR-0045 §13-14）

### 历史持久化

- 路径：`~/.openshell/history.jsonl`
- 每行一条 JSON：`{"cmd": "...", "ts": "...", "exit": 0}`
- 上限 10000 条，FIFO 淘汰
- 启动时加载，关闭时保存（也支持增量写）

## Alternatives Considered

1. **直接用 PrettyPrompt 不做抽象**：被否决，PrettyPrompt 升级或替换会牵动 ReplEngine；测试无法 mock 行编辑。
2. **从零自研行编辑器**：被否决，跨平台终端处理（VT100 / Unicode 宽字符 / 信号）工作量极大，PrettyPrompt 已成熟。
3. **GNU Readline / linenoise 绑定**：被否决，C 绑定维护成本高，跨平台构建复杂。
4. **PowerShell 风格自己写 PSReadLine**：被否决，PSReadLine 是 50k+ 行代码量级，不适合 M1。
5. **同步 `Console.ReadLine` 直到 M3**：被否决，Tab 补全是 CLI 的基本体验，不能等到 M3。

## Consequences

### 优势
- 每层可独立替换，测试可注入 mock
- PrettyPrompt 现成功能省去 80% 工作
- 跨平台 ANSI 处理交给 PrettyPrompt
- 历史/补全数据源解耦，可独立测试

### 代价
- PrettyPrompt 是同步 API（`ReadAsync` 返回 `Task<string>`），适配成 `LineEditResult` 需额外封装
- PrettyPrompt 的多行支持有限，复杂续行需自研补丁
- 引入 PrettyPrompt 依赖（约 200KB）

### 约束
- 所有 IO 经接口，不直接调 `Console.Write`，便于测试
- ANSI 渲染必须在 `IAnsiSequences` 探测后决定能力降级，禁止无条件输出 ANSI 转义
- 历史文件包含用户命令，可能含敏感信息（密码），需文件权限 0600
- 行编辑器接口必须接受 `CancellationToken`，Ctrl+C 时立即返回
- 多行输入的续行提示符（`... `）必须与单行提示符明显区分
- Prompt 渲染依赖 `CurrentLocation.Display`，不依赖未公开的 host 状态
