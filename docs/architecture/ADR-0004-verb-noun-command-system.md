# ADR-0004: 命令系统采用 Verb-Noun + ICommand<TArgs> + 反射注册

- **Status**: Accepted
- **Date**: 2026-07-07
- **Decider**: Architecture
- **Supersedes**: —

## Context

CLI 与 GUI 是两个等价的 host，都通过 `CommandDispatcher` 调用 Core。命令系统的设计必须满足：

- CLI：自动生成参数解析器（`--help`、位置参数、别名、`-r` 短选项）
- GUI：自动生成菜单项 / 工具栏按钮 / 右键菜单
- Pipeline：`get-childitem | where size > 1MB | select name` 可链式组合
- 一致性：动词受约束枚举（Get/Set/New/Remove/Move/Copy/...），避免 `list`/`ls`/`dir` 各写一份

PowerShell 的 Cmdlet 用 `[Cmdlet(VerbsCommon.Get, "ChildItem")]` + `[Parameter]` 特性，证明可行。但 PS 的 Cmdlet 类继承 `PSCmdlet`，带强耦合基类，不利于测试。

## Decision

采用**声明式命令 + 反射注册 + 无强基类**：

```csharp
[Verb("Get", Noun = "ChildItem")]
[Description("Lists items in a container")]
public sealed class GetChildItemCommand : ICommand<GetChildItemCommand.Args>
{
    public record Args
    {
        [Parameter(Position = 0)] public ItemPath? Path { get; init; }
        [Parameter(Aliases = new[]{"-f"})] public string? Filter { get; init; }
        [Parameter(Aliases = new[]{"-r"})] public bool Recurse { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct)
    { ... }
}
```

约束：
1. **Verb 是受约束枚举**：`Get/Set/New/Remove/Move/Copy/Rename/Invoke/Select/Where/Sort/Format/Out/...`，新增 Verb 需评审
2. **命令类无状态**：每个调用新建实例（DI `Transient`），不持有可变字段
3. **Args 是 `record`**：与 ADR-0003 一致，不可变
4. **注册方式**：启动时扫描程序集，按 `[Verb]` 特性注册到 `CommandRegistry`，CLI/GUI 共用同一注册表

## Alternatives Considered

1. **直接 `System.CommandLine` API 注册**：被否决，命令逻辑与参数声明分离，反射损耗小但代码组织混乱。
2. **Source Generator 生成注册代码**：被否决，引入构建复杂度，首期反射开销可接受（命令解析而非每条管道元素执行）。
3. **函数式声明**（`Command.Define("get-childitem", ...)`）：被否决，类型安全弱，参数从 `object` 取出。
4. **PowerShell 风格继承 `CmdletBase`**：被否决，强基类耦合、单测需 mock 基类上下文。

## Consequences

### 优势
- CLI 自动从 `Args` 生成参数解析器、`--help`、补全候选
- GUI 自动从 `[Verb]` 注册菜单项，参数从 UI 收集器收集
- 命令类无强基类，单测直接 `new XxxCommand().ExecuteAsync(args, ctx, ct)`
- Pipeline 节点（`where`/`select`/`sort`）也是同款 `ICommand`，统一调度

### 代价
- 反射注册在启动时有少量开销（一次性，<10ms，可接受）
- Args 必须是 `public record`，所有字段 `init`，约束略严
- 命令类不能持有状态，跨调用共享状态需走 `CommandContext`

### 约束
- 命令类必须 `sealed`，便于 JIT 内联，且防止被继承破坏无状态约束
- 命令类通过 DI 注入依赖（如 `IProviderRegistry`），不在构造里做 IO
- 同名命令（Verb+Noun）禁止重复注册，启动时检测，重复则抛异常 fail-fast
- Pipeline 中间节点（`where`/`select`/`sort`/`format-*`/`out-*`）实现 `IPipelineCommand` 接口，与 `ICommand` 区分以避免在 GUI 菜单里出现
