# ADR-0025: 帮助系统

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0004 (命令系统), ADR-0023 (命令清单), ADR-0022 (配置)

## Context

CLI 用户发现命令能力的主要途径是帮助系统。需求：

1. `get-help <command>` / `<command> --help` / `<command> -?` 三种入口
2. `get-command` 列出所有命令、按 Verb/Noun/Group 筛选
3. `get-verb` 列出受约束动词
4. `about_*` 主题文档（`about_providers`、`about_pipeline`、`about_aliases`）
5. 命令示例（`-Examples`）
6. 在线文档链接（`-Online`）
7. 详细 vs 简短（`-Detailed` / `-Full`）
8. GUI 命令面板也能展示帮助摘要
9. 帮助内容多语言（ADR-0035 i18n 衔接）

PowerShell 的 `Get-Help` 帮助来自：

- 命令代码注释（`<help />` XML）
- `Get-Help` XML 文件
- 在线 MAML 文档

我们简化但保留多来源。

## Decision

### 1. 帮助来源（按优先级）

| 优先级 | 来源 | 用途 |
|---|---|---|
| 1 | 命令特性 `[Description]` + `[Parameter(HelpText)]` | 简短摘要 |
| 2 | `docs/commands/<command>.md` | 完整文档 + 示例 |
| 3 | `~/.openshell/help/<command>.md` | 用户覆盖 |
| 4 | 在线 `https://openshell.dev/commands/<command>` | 最新版本 |

查找顺序：用户覆盖 > 内置 md > 特性摘要 > 在线。

### 2. 命令特性增强

```csharp
[Verb("Get", Noun = "ChildItem", Aliases = new[]{"ls", "dir"})]
[Description("Lists items in a container.")]
[Help(Synopsis="Enumerates children of a container, optionally filtering and recursing.",
      Examples = new[]{
          "get-childitem                       # list current directory",
          "get-childitem -r                    # recursive",
          "get-childitem -f *.txt              # filter by glob"
      },
      OnlineUrl="https://openshell.dev/commands/get-childitem")]
public sealed class GetChildItemCommand : ...
{
    public record Args(
        [property: Parameter(Position = 0)]
        [property: Description("Path to enumerate. Defaults to current location.")]
        ItemPath? Path = null,
        ...);
}
```

### 3. Get-Help 命令

```
get-help get-childitem
get-help get-childitem -detailed
get-help get-childitem -examples
get-help get-childitem -full
get-help get-childitem -online
get-help about_providers
```

输出结构：

```
NAME
    get-childitem

SYNOPSIS
    Lists items in a container.

SYNTAX
    get-childitem [[-Path] <ItemPath>] [-Filter <string>] [-Recurse] [<CommonParameters>]

DESCRIPTION
    Enumerates children of a container, optionally filtering and recursing.
    Works across all providers that implement IContainerProvider.

PARAMETERS
    -Path <ItemPath>
        Path to enumerate. Defaults to current location.

    -Filter <string>
        Glob filter, e.g. *.txt

    -Recurse [<SwitchParameter>]
        Recurse into subdirectories.

EXAMPLES
    -------------------------- EXAMPLE 1 --------------------------
    get-childitem
    Lists current directory.

    -------------------------- EXAMPLE 2 --------------------------
    get-childitem -r
    Recursive listing.

RELATED LINKS
    get-item
    set-location
    https://openshell.dev/commands/get-childitem
```

### 4. --help / -? 短形式

```
get-childitem --help
```

仅输出 `SYNOPSIS` + `SYNTAX` + `PARAMETERS`，不含示例。

`-?` 等价 `--help`。

### 5. Get-Command

```
get-command                       # 全部
get-command -Verb Get             # 按 Verb
get-command -Noun *Item*          # 按 Noun glob
get-command -Group Core           # 按组
get-command -Type Alias           # 仅别名
get-command -Type Function        # 仅函数
```

输出表格：

```
CommandType    Name                  Source       Group
Command        get-childitem         Builtins     Core
Command        copy-item             Builtins     Core
Alias          ls                    user         -
Function       find-large            user         -
```

### 6. Get-Verb

```
get-verb
```

输出受约束动词枚举与说明：

```
Verb      Group        Description
Get       Common       Retrieve resources
Set       Common       Modify existing resources
New       Common       Create new resources
Remove    Common       Delete resources
...
```

### 7. about_* 主题文档

`docs/about/*.md` 内置主题：

- `about_providers.md` — Provider 模型
- `about_pipeline.md` — 管道
- `about_aliases.md` — 别名
- `about_functions.md` — 函数
- `about_path_syntax.md` — 路径语法
- `about_filter_dsl.md` — Filter DSL
- `about_formatting.md` — 格式化
- `about_unDo.md` — Undo/Redo
- `about_remote.md` — 远程 Provider

`get-help about_providers` 直接渲染对应 md。

### 8. 用户覆盖

`~/.openshell/help/<command>.md` 存在时优先使用，便于：

- 团队内部约定（如命令别名 `ll` 的团队说明）
- 翻译未支持的语言（i18n 前的过渡）

### 9. GUI 命令面板帮助

Ctrl+Shift+P 命令面板（ADR-0027）的每条命令显示 `Synopsis` 一行描述，按 F1 弹完整帮助窗口。

### 10. 帮助的多语言

特性描述默认英文，`<command>.md` 文件可命名 `<command>.zh-CN.md` / `<command>.ja.md`：

- 按用户 locale 自动选择
- 未找到时降级到无 locale 后缀
- 兜底到特性英文

详细 i18n 见 ADR-0035。

### 11. 帮助的渲染

CLI：纯文本 + ANSI 颜色（标题加粗、参数 dim）。
GUI：Markdown 渲染（Avalonia Markdown 控件）。

### 12. 帮助的更新

- 内置帮助随版本发布
- 在线帮助是 GitHub Pages 上的文档站
- `update-help` 命令可下载最新 md 到 `~/.opensshell/help/`（类似 PowerShell `Update-Help`）

### 13. 帮助写作规范

`docs/commands/<command>.md` 模板：

```markdown
---
command: get-childitem
synopsis: Lists items in a container.
---

# Get-ChildItem

## SYNOPSIS
...

## SYNTAX
...

## DESCRIPTION
...

## PARAMETERS
...

## EXAMPLES
...

## RELATED LINKS
...
```

frontmatter 用于 `get-command` 索引。

## Alternatives Considered

1. **仅命令特性描述**：被否决，无法承载示例、详细说明
2. **PowerShell MAML XML**：被否决，XML 写作负担重
3. **从代码注释生成**：被否决，注释与文档定位不同
4. **仅在线文档**：被否决，离线场景失效
5. **`man` 风格分页**：被否决，跨平台终端分页难，但提供 `| less` 管道支持

## Consequences

### 优势
- 三种入口覆盖用户习惯
- 多来源优先级清晰
- 用户可覆盖
- 多语言预留接口
- GUI 与 CLI 复用

### 代价
- 文档与代码同步维护成本
- 在线文档站建设成本
- `update-help` 需网络

### 约束
- 命令必须声明 `[Description]`，否则启动时警告
- 参数必须声明 `[Description]` 或 `HelpText`，否则 `--help` 显示空
- `docs/commands/*.md` 必须有 frontmatter
- `get-help` 输出必须支持 ANSI 与纯文本降级（按 ADR-0008 `IAnsiSequences`）
- 用户覆盖文件解析失败时降级到内置
- 在线 URL 必须是稳定路径（`/commands/<name>`），不允许版本路径
- `about_*` 主题名禁止下划线以外的特殊字符
- `update-help` 必须支持断点续传
- 帮助内容禁止含运行时凭据
