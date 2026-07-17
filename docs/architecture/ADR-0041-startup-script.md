# ADR-0041: 启动脚本 / $PROFILE 机制

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (CLI REPL), ADR-0022 (配置), ADR-0024 (别名), ADR-0042 (自动变量)
- **Revised**: 2026-07-08 (per ADR-0045/0042 revised)

## Context

ADR-0024 已确立 `aliases.toml` / `functions.toml` 作为静态声明机制，但存在以下场景无法满足：

1. **条件分支缺失**：无法表达"在 Windows 上设这个别名、在 Linux 上设另一个"，更无法根据 `$HOST` 类型分支。
2. **启动期命令缺失**：无法在启动时跑一段命令——加载模块、初始化环境变量、自定义提示符、cd 到默认目录。
3. **状态依赖的初始化**：某些初始化依赖运行时探测结果（如 `test-path` 某目录后再 `set-alias`），静态 TOML 无法表达。
4. **跨项目特化**：项目级 `.openshell/aliases.toml` 仅支持别名声明，不支持"进入此项目时执行一段脚本"的需求。

参考 PowerShell `$PROFILE` 机制：用户在启动脚本里写一段命令，Shell 启动时按序执行，从而完成动态初始化。OpenShell 需要一个等价的、但风格统一的启动脚本机制。

参考：

- PowerShell `$PROFILE` / `$PROFILE.CurrentUserCurrentHost`
- Bash `.bashrc` / `.bash_profile`
- Zsh `.zshrc`

## Decision

### 1. 脚本位置

参考 ADR-0022 的目录结构，采用三层 profile 文件机制：

| 层级 | 路径 | 用途 |
|---|---|---|
| 用户全局 | `~/.openshell/profile.openshell` | 用户默认启动脚本 |
| 项目级 | `<cwd>/.openshell/profile.openshell` | 项目特化脚本，进入该 cwd 时加载 |
| 命令行覆盖 | `--profile <path>` | 显式指定 profile，跳过默认查找 |
| 跳过加载 | `--noprofile` | 测试 / 故障排查用，不加载任何 profile |

跨平台路径解析沿用 `OpenShellPaths.Root`（见 ADR-0022）。

命令行参数优先级：

- `--noprofile` 最高，命中时跳过所有 profile 加载（含用户全局与项目级）。
- `--profile <path>` 显式指定时，仅加载该文件，跳过用户全局与项目级自动查找。
- 未指定参数时，按"用户全局 → 项目级"顺序加载。

### 2. 加载顺序

完整启动序列（后者覆盖前者）：

```
1. 内置（命令系统、Provider 注册）
2. 内置别名（[Verb(Aliases)] 特性）
3. 内置命令（Cmdlet 注册）
4. 用户全局 aliases.toml
5. 用户全局 functions.toml
6. 用户全局 profile.openshell   ← 本 ADR 引入
7. 项目级 profile.openshell     ← 本 ADR 引入
8. REPL 循环 / GUI 主窗口
```

依据：

- profile 必须在 aliases / functions 已加载后执行，否则脚本内 `set-alias` 无法引用已声明的别名。
- 项目级 profile 在用户全局之后执行，可覆盖用户全局的副作用（如再次 `cd`）。
- 两者均在 REPL 启动前完成，确保用户第一条命令进入已就绪的会话状态。

### 3. 执行时机

| Host | 时机 |
|---|---|
| CliHost | `CliHost.RunAsync` 启动 REPL 循环**前** |
| GuiHost | `MainWindow` 显示**前** |

执行机制：

- 脚本逐行读取，每行送入**同一个** `DispatchAsync` 调度管线（复用命令系统，不另起解析器）。
- 注释行（`#` 开头）与空行跳过。
- 多行命令（首期不支持，预留扩展）：以 `\` 结尾的行视为续行。
- profile 内允许 `. <path>`（dot-source）嵌套加载其他 `.openshell` 脚本，递归深度上限 5，避免循环引用。

### 4. 错误处理

参考 ADR-0026 的错误分级，profile 执行期间 `ErrorRecord` 走正常 `IErrorStream`：

| 错误类别 | 默认行为 | 说明 |
|---|---|---|
| `ParseError` | 中断脚本 | 语法错误，后续语句无意义 |
| `ConfigurationError` | 中断脚本 | 配置 / 别名加载失败 |
| `ItemNotFound` / `ProviderNotFound` | 警告并继续 | 可能是条件性引用，不应阻塞 |
| `CommandNotFound` | 警告并继续 | 项目级 profile 引用了项目特有的命令 |
| `PermissionDenied` | 警告并继续 | 某条初始化失败但会话仍可用 |

可配置：

```toml
# ~/.openshell/config.toml
[profile]
stopOnError = true          # 默认 true；false 时所有错误降级为警告继续
```

`--noprofile` 与 `stopOnError = false` 的组合用于故障排查：用户可临时禁用 profile 加载，或允许 profile 出错时仍启动会话。

profile 执行期间产生的 `ErrorRecord` 全部写入 `IErrorStream`，REPL 启动后用户可通过 `get-error` 查看。

### 5. 作用域

参考 ADR-0024 三层别名机制，profile 内的副作用分为以下作用域：

| 操作 | 作用域 | 持久化 |
|---|---|---|
| `set-alias` / `set-function` | Session 级（最高优先级） | 进程生命周期 |
| `cd` 修改 `CurrentLocation` | Session 级 | 持续到 REPL 启动后，用户可见 |
| `set-variable`（未来变量系统） | Session 级 | 进程生命周期 |
| `set-promptstyle` | Session 级 | 覆盖 config.toml 的 `[shell] promptStyle` |
| `set-theme` | Session 级 | 覆盖 config.toml 的 `[theme]` 配置 |

依据：

- profile 是"会话级初始化"，所有副作用默认 Session 级，最高优先级覆盖用户全局 TOML。
- `cd` 副作用持续到 REPL 启动后——这是用户期望的"启动后跳到默认目录"行为。
- Session 级状态不会被 `config.toml` 热重载覆盖（见 ADR-0022 §4）。

### 6. 脚本语法

复用 OpenShell 命令语法，不引入新解析器：

| 语法元素 | 支持 | 说明 |
|---|---|---|
| Verb-Noun 命令 | ✅ | `set-alias` / `get-childitem` 等 |
| 别名调用 | ✅ | 复用 ADR-0024 别名表 |
| 函数调用 | ✅ | 复用 ADR-0024 函数表 |
| 全局开关 | ✅ | `set-promptstyle` / `set-theme` |
| 管道 `\|` | ✅ | 复用命令系统管道 |
| dot-source `. <path>` | ✅ | 嵌套加载脚本 |
| 行注释 `#` | ✅ | 行首或行尾均可 |
| 续行 `\` | ✅ | 行尾反斜杠 |
| 控制流 `if` / `for` / `while` | ✅ | 支持（per ADR-0045） |
| 变量赋值 `$x = ...` | ❌（首期） | 等待变量系统 |

不支持控制流的依据：

- 保持 profile 简单——条件分支用 `if-exists` / `test-path` 等单命令表达。
- 复杂逻辑应封装为函数（ADR-0024）或 C# 插件命令，而非堆在启动脚本里。
- 避免 profile 演化为"小型脚本语言"，增加维护与调试成本。

`if-exists` 命令约定（profile 内常用）：

```
if-exists <path> { <command-block> }
if-exists .openshell/tools.openshell { . .openshell/tools.openshell }
```

仅当 `<path>` 存在时执行 `<command-block>`，否则跳过——这是 profile 内做条件分支的推荐方式。

### 7. 可用变量

profile 中可用的特殊自动变量（详见 ADR-0042）：

| 变量 | 含义 | 示例值 |
|---|---|---|
| `$PROFILE` | 当前 profile 文件路径 | `C:\Users\alice\.openshell\profile.openshell` |
| `$PROFILE.AllUsersAllHosts` | 全用户全 Host profile 路径（保留字段，未实现） | `C:\ProgramData\OpenShell\profile.openshell` |
| `$PROFILE.CurrentUserAllHosts` | 当前用户全 Host profile 路径 | `~/.openshell/profile.openshell` |
| `$PROFILE.CurrentUserCurrentHost` | 当前用户当前 Host profile 路径（Cli / Gui 区分） | `~/.openshell/profile.openshell` |
| `$Host` | HostObject | 完整宿主对象（per ADR-0042 §3.2 revised）；`$Host.Name` 返回 "Cli" / "Gui" |

简化说明：

- 沿用 PowerShell 多字段习惯，但首期仅单一文件 `profile.openshell`，三个字段返回相同路径。
- 预留 `AllUsersAllHosts` 字段为未来系统级 profile 扩展占位。
- `$HOST` 用于在 profile 内做 Host 类型分支：`if-exists` 配合 `set-variable` 可实现"Gui 时设这个提示符、Cli 时设另一个"。

### 8. 热重载

与 ADR-0022 配置热重载不同，**profile 不支持热重载**：

- profile 是一次性执行的脚本，副作用已落到 Session 状态。
- 修改 profile 后需重启会话生效。
- 不监视 profile 文件变化，不触发 `IOptionsMonitor.OnChange`。

`reload-profile` 命令：

- 清空当前 Session 级状态（别名、函数、变量、提示符设置等）。
- 重新执行 profile（按上述加载顺序第 6-7 步）。
- 不重新加载内置命令 / aliases.toml / functions.toml（这些由 ADR-0022 配置热重载负责）。
- 执行期间错误处理同 §4。

`reload-profile` 是 profile 修改后无需重启会话的唯一手段——但必须接受"清空 Session 状态"的代价（用户运行时临时设置的别名 / 变量会丢失）。

### 9. 示例 profile.openshell

```openshell
# 我的 OpenShell 启动脚本

# 短缩写别名
set-alias ll "get-childitem -l"
set-alias gp "get-itemproperty"
set-alias touch "new-item -type file"

# 项目特化：进入含 tools.openshell 的项目时加载工具脚本
if-exists .openshell/tools.openshell { . .openshell/tools.openshell }

# 自定义提示符
set-promptstyle "full"

# 默认跳到常用目录
cd fs::$HOME/Projects
```

Gui Host 下条件分支示例：

```openshell
# profile.openshell
if-exists fs::$HOME/.openshell/gui-theme.openshell {
    . fs::$HOME/.openshell/gui-theme.openshell
}
```

项目级 profile 示例（`<project>/.openshell/profile.openshell`）：

```openshell
# 项目级启动脚本
set-alias build "invoke-msbuild -config Release"
set-alias test "dotnet test"

# 项目根作为默认 cwd
cd fs::$PROJECT_ROOT
```

## Alternatives Considered

1. **PowerShell 完全兼容 `$PROFILE` 多文件方案**：被否决。PowerShell 区分 `AllUsersAllHosts` / `AllUsersCurrentHost` / `CurrentUserAllHosts` / `CurrentUserCurrentHost` 四个文件，加载顺序复杂、文件爆炸。OpenShell 首期单一文件 `profile.openshell` 足够，多文件需求可由 dot-source 拆分。

2. **Python 式 `.bashrc` 全局变量方案**：被否决。仅暴露变量不够——用户需要在启动时跑命令（`cd` / `set-alias`），纯变量机制无法满足。

3. **不支持启动脚本**：被否决。条件性别名、启动期命令、跨项目特化是常见需求，缺失会显著影响用户体验，迫使用户每次手动执行初始化命令。

4. **嵌入完整脚本语言（Lua / JS / Python）**：被否决。引入外部依赖、安全风险、维护成本，且与 OpenShell 命令系统割裂。复用 OpenShell 命令语法 + `if-exists` 已覆盖 90% 场景。

5. **仅支持 TOML 声明扩展（如 `[init]` 段）**：被否决。TOML 无法表达顺序依赖的命令序列，也无法做 `if-exists` 条件，能力受限。

6. **profile 支持完整控制流（if/for/while）**：已由 ADR-0045 (2026-07-08) 取代。原否决理由（profile 会演化为"小型脚本语言"，调试 / 维护成本高；复杂逻辑应封装为函数或插件）不再适用——ADR-0045 已统一确立控制流语义，profile 与函数体共享同一求值路径。

## Consequences

### 优势

- **用户体验**：用户可在启动时完成日常初始化（别名、cd、提示符），无需每次手动执行。
- **定制能力**：条件分支（`if-exists`）+ Host 类型分支（`$HOST`）覆盖跨平台 / 跨项目场景。
- **复用现有系统**：脚本送入 `DispatchAsync`，不引入新解析器，错误处理复用 ADR-0026。
- **三层优先级清晰**：用户全局 → 项目级 → 命令行覆盖，与 ADR-0024 别名机制一致。
- **故障排查友好**：`--noprofile` 与 `--profile <path>` 提供干净环境；`stopOnError = false` 允许 profile 出错时仍启动。
- **热重载替代**：`reload-profile` 提供不重启会话的更新手段。

### 代价

- **额外复杂度**：Host 启动流程增加 profile 加载阶段，需处理加载顺序、错误中断、作用域持久化。
- **profile 损坏可能阻塞启动**：`ParseError` / `ConfigurationError` 默认中断脚本，若 profile 早期语句即出错，用户无法进入会话。缓解：`--noprofile` 与 `stopOnError = false`。
- **`reload-profile` 必须清状态**：用户运行时临时设置的别名 / 变量会丢失，需在文档中明确告知。
- **不支持控制流**：复杂初始化逻辑需拆分为函数或插件，用户需理解边界。
- **作用域持久化复杂**：`cd` 副作用持续到 REPL 启动后，需确保所有 Host 在 profile 执行完毕后才进入主循环。

### 约束

- profile 必须在 `aliases.toml` / `functions.toml` 加载后执行，否则脚本内 `set-alias` 无法引用已声明的别名。
- profile 不支持热重载，修改后需重启会话或执行 `reload-profile`。
- `reload-profile` 必须清空当前 Session 级状态（别名、函数、变量、提示符设置），仅保留内置与配置文件层。
- profile 脚本支持控制流（per ADR-0045，2026-07-08 修订）。
- profile 内 `set-alias` 设置的别名为 Session 级，优先级高于用户全局 `aliases.toml`。
- profile 内 `cd` 修改的 `CurrentLocation` 必须持久到 REPL 启动后，用户首条命令进入已切换的目录。
- `--noprofile` 必须跳过所有 profile 加载（用户全局 + 项目级），不读取 `~/.openshell/profile.openshell`。
- `--profile <path>` 指定时仅加载该文件，跳过用户全局与项目级自动查找。
- profile 执行期间的 `ErrorRecord` 必须写入 `IErrorStream`，REPL 启动后用户可通过 `get-error` 查看。
- `stopOnError` 默认 `true`；致命错误（`ParseError` / `ConfigurationError`）中断脚本，警告类（`ItemNotFound` 等）继续。
- `$PROFILE` 及其子字段（`AllUsersAllHosts` / `CurrentUserAllHosts` / `CurrentUserCurrentHost`）在 profile 内可读，首期均返回同一文件路径。
- `$Host.Name` 返回 `"Cli"` 或 `"Gui"`（`$Host` 本身为 `HostObject`，per ADR-0042 revised），用于 Host 类型分支。
- dot-source（`. <path>`）嵌套深度上限 5，必须检测循环引用（`a.openshell` dot-source `b.openshell` dot-source `a.openshell` 报错）。
- profile 文件必须以 UTF-8 编码读取，BOM 可选。
- profile 文件权限（Unix）建议 0644，不含凭据；如需凭据应通过 `ICredentialProvider`（见 ADR-0022 §5）访问，禁止在 profile 中硬编码。
- `reload-profile` 命令必须记录操作日志到 `journal.jsonl`，便于审计 Session 状态变更。
