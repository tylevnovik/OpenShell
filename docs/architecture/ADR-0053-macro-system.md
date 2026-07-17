# ADR-0053: Macro System — 声明式宏 (macro_rules!)

- **Status**: Accepted
- **Date**: 2026-07-08
- **Stage**: M5+ (Language Layer)
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0050 (Modern Syntax, §11.4), ADR-0056 (Module System, macro_export)
- **Implementation Status**: M5+ 已实现 (2026-07-08): `macro_rules!` 定义解析、`name!(...)` 调用解析、MacroDefinitionStatement / MacroInvocationExpression AST、MacroRegistry 注册表、MacroExpander 模式匹配 + token 替换 + 重解析、内置宏 `println!` / `dbg!` / `assert!` / `assert_eq!`、递归深度保护 (64)。

## Context

ADR-0050 §11.4 把宏系统推迟到独立 ADR。当前 OpenShell 无任何宏机制。用户在以下场景反复出现"模板代码冗余"痛点：

1. **样板命令调用**：`Write-Host "..."` / `Write-Error "..."` 等命令式调用噪音大，希望 `println!("...")` 风格。
2. **调试断言**：`if (-not $cond) { throw "..." }` 啰嗦，希望 `assert!($cond)` 一行表达。
3. **DSL 扩展**：用户希望定义领域特定简写（如 `vec!(1, 2, 3)` 展开为数组构造 + 校验），而不必写命令 / 函数。

Rust `macro_rules!` 是经过工业验证的声明式宏模型：模式匹配 + token 替换 + 卫生性，足够覆盖上述场景且实现复杂度可控。本 ADR 引入 Rust 风格声明式宏。

## Decision

### 1. 宏定义语法

```
macro_rules! name {
    ($pattern) => { expansion }
    ($pattern2) => { expansion2 }
}
```

- `macro_rules!` 由 tokenizer 产出 `Identifier("macro_rules")` + `Bang` 两个 token；parser 在 statement 起始检测该序列。
- 多 arm：每个 arm 是 `(pattern) => { expansion }`，pattern 与 expansion 均为 **原始 token 列表**（不在定义时解析为 AST，推迟到展开时）。
- pattern 与 expansion token 在 `MacroDefinitionStatement` 中以 `IReadOnlyList<Token>` 存储。

### 2. 模式匹配与捕获

- 捕获语法：`$name:type`，其中 `type` 取自下列片段分类（fragment specifier）：

| specifier | 匹配 | 捕获 |
|---|---|---|
| `expr` | 一个表达式（token 序列直到下一个顶层分隔符） | token 列表 |
| `ident` | 一个标识符 token | 单 token |
| `ty` | 一个类型引用 token 序列 | token 列表 |
| `block` | `{ ... }` 块 token 序列 | token 列表 |
| `tt` | 单个 token tree（平衡括号内） | token 列表 |

- 字面量 token：pattern 中非 `$` token 必须与调用 token 精确匹配（Text 相等）。
- 匹配算法（v1，贪心）：从左到右扫描 pattern；遇字面量比对调用 token；遇捕获按 specifier 贪心消费到下一个 pattern 字面量或结束。逗号 `,` 作为常见分隔符按字面量匹配。
- v1 实现完整支持 `expr` / `ident` / `tt`；`ty` / `block` 退化为 `tt` 语义（按 token tree 消费）。

### 3. 宏调用语法

```
name!(arg1, arg2)
name!{ arg1, arg2 }     // 花括号调用形式（Rust 风格）
```

- parser 在 `ParsePrimary` 的 Identifier 分支检测 `Identifier ! ( ` 或 `Identifier ! {`，构造 `MacroInvocationExpression(Name, ArgumentTokens, Span)`。
- 参数以 **原始 token 列表** 存储（含分隔逗号），供展开时按 pattern 匹配。

### 4. 展开（Expansion）

- **展开时机**：求值时（lazy）。`MacroInvocationExpression` 在 Evaluator 中求值时触发展开，而非 parse 时。理由：parse 时宏定义可能尚未执行（前向引用），且求值时展开可访问运行时注册表。展开产物为 `Expression`，立即求值。
  - 注：ADR-0050 §11.4 倾向 parse-time，但 OpenShell 的"定义即语句执行"模型要求 lazy 展开以保证顺序。parse-time 展开作为 open question。
- **MacroExpander**（`OpenShell.Macros`）：
  1. 查 `MacroRegistry` 取定义。
  2. 逐 arm 匹配：提取 `Dictionary<string, IReadOnlyList<Token>>` 捕获。
  3. 替换：扫描 expansion token，遇 `$name`（Variable token）替换为捕获 token 列表。
  4. 重解析：把替换后 token 列表交 `ModernParser.ParseExpression` 重新解析为 `Expression`。
  5. 返回 `Expression`；匹配失败返回 null。
- **递归深度**：展开结果可能含新的 `MacroInvocationExpression`，递归求值时深度计数；超过 64 抛 `OpenShellScriptException("macro recursion limit exceeded")`。
- **卫生性（Hygiene, v1 简化）**：捕获 token 原样替换，不做 gensym。展开作用域内引用的标识符按词法作用域解析。完整卫生性（捕获变量不与展开域冲突）作为 open question。

### 5. 内置宏

预定义宏（不在 registry，由 Evaluator 直接识别名 + 求值参数）：

| 宏 | 语义 |
|---|---|
| `println!(expr...)` | 求值参数，格式化输出到 IHost + 换行 |
| `dbg!(expr)` | 求值参数，输出 `[dbg] = value` 到 IHost |
| `assert!(expr)` | 求值，非真抛 `OpenShellScriptException("assertion failed")` |
| `assert_eq!(a, b)` | 求值两者，不等抛异常（含双方值） |

- 内置宏参数先按顶层逗号切分，每段用 `ModernParser.ParseExpression` 解析为 `Expression`，再求值。
- `println!` 单字符串参数直接输出；多参数以空格连接（v1 不实现 `{0}` 格式化，标记 open question）。

### 6. 模块导出（macro_export）

- `macro_rules!` 定义的宏默认模块内可见。
- `#[macro_export]` 属性（future，依赖 ADR-0056 模块系统）将宏注册到全局 registry，跨模块可见。v1 所有宏注册到当前 `ExecutionContext.Macros`（单会话作用域）。

## Costs

- **求值开销**：宏调用每次展开都做模式匹配 + token 替换 + 重解析。热路径不鼓励宏；内置宏直接求值无重解析开销。
- **错误定位**：展开后代码的错误信息指向展开产物，而非宏调用点。v1 接受此限制（open question：span 透传）。
- **复杂度**：MacroExpander ~200 行，parser hook ~80 行。

## Alternatives

- **C 风格文本宏（`#define`）**：无结构、易冲突、无卫生性，否决。
- **Lisp 风格 hygiene 宏（syntax-case）**：实现复杂度远超收益，否决。
- **编译期函数（compile-time functions）**：需要独立的编译期求值器，v1 不具备，推迟。
- **parse-time 展开**：前向引用问题与顺序依赖，v1 用 lazy 展开，parse-time 作为 open question。

## Open Questions

1. parse-time vs eval-time 展开：是否在完成模块加载顺序后切换到 parse-time 以支持更优错误信息？
2. 完整卫生性：捕获变量是否需要 gensym 以避免与展开域标识符冲突？
3. `println!` 格式化：是否实现 Rust `{}` / `{0}` 占位符格式化？
4. span 透传：展开产物的 SourceSpan 是否回链到宏调用点？
5. `macro_export` 与 ADR-0056 模块系统的精确集成。

## Constraints

- 递归深度硬上限 64，防止无限展开。
- 内置宏名（`println` / `dbg` / `assert` / `assert_eq`）保留，用户定义同名宏被内置优先（v1）。
- 宏调用与一元 `!`（逻辑非）歧义：`name!(...)` 检测要求 `!` 紧跟标识符且后随 `(` 或 `{`，否则 `!` 按一元运算符处理。
- 代码注释中文（遵循 codebase 约定）。
