# 现代语法（.osh）实现审计报告

- **审计日期**: 2026-07-10
- **审计基准**: ADR-0050-modern-syntax.md（§1–§10 + 约束 + Open Questions）
- **构建基线**: 0 警告 0 错误（`dotnet build OpenShell.slnx`）
- **审计结论**: ADR-0050 头部自称「§1–§10 已实现」，但实际存在大量偏差与缺项。本报告逐条记录实现状态，作为修复工作的依据。

> 本文件为**外置待做文档**的一部分。修复进度由 `docs/modern-syntax-tasks.md` 追踪，验证由 `ModernSyntaxComplianceTests` 测试套件保障。详见 `agents.md`「任务追踪规范」。

---

## 一、严重度分级

- **P0 违反规范（行为错误）**：实现与 ADR 明确冲突，破坏兼容性承诺或核心语义。
- **P1 未实现（ADR 明确要求）**：ADR 要求但代码中找不到对应实现。
- **P2 部分实现**：实现存在但有缺陷或不符合规范细节。
- **P3 测试薄弱**：实现可能正确但缺乏验证。

---

## 二、逐节审计结果

### §1 双语法架构

| 项 | 状态 | 证据 |
|---|---|---|
| `.osh`/`.ps1` 文件后缀判定 | ✅ 已实现 | `Evaluator.cs` 按 `Path.GetExtension` 选 parser |
| `#lang ps1`/`#lang osh` REPL 切换 | ✅ 已实现 | `Program.cs` `TryHandleLangDirective` |
| `#lang ps1 { ... }` 块切换 | ❌ P1 未实现 | 全仓无 `UnclosedLangBlock`/块级 `#lang` 处理 |
| 无后缀文件默认现代语法 + `ParseWarning` | ❌ P1 未实现 | 未找到该警告逻辑 |
| 单块内不混用检查 | ❌ P1 未实现 | Tokenizer 单模式，两套运算符 token 并存 |

### §2 操作符现代化

| 项 | 状态 | 证据 |
|---|---|---|
| `==` `!=` `>` `<` `>=` `<=` | ✅ | Tokenizer + ModernParser.TryGetBinaryOp |
| `&&` `\|\|` `!` | ✅ | 同上 |
| `~=`（通配符匹配） | ❌ P1 | 无 `~=` token |
| `~regex`（正则匹配） | ❌ P1 | 无 `~regex` token |
| `in`（modern 二元运算符） | ❌ P1 | `-in` 存在但 ModernParser.TryGetBinaryOp 未列入 |
| `contains`（modern） | ❌ P1 | 同上未列入 |
| `++`（数组拼接） | ❌ P1 | `++` 仅作递增 |
| `.osh` 下 PS 形式 emit `DeprecationWarning` | ❌ P1 | 全仓无 `DeprecationWarning`；ModernParser 反而显式接受 PS 运算符 |
| `.ps1` 下现代形式不识别 | ❌ **P0 违反规范** | PowerShellParser.TryGetBinaryOp（:1279-1296）显式接受 `Equals`/`NotEquals`/`Lt`/`Gt`/`Le`/`Ge`、`PipePipe`/`AmpAmp`、`Bang`、三元 `?` —— 破坏 PS 兼容性承诺 |

### §3 函数语法

| 项 | 状态 | 证据 |
|---|---|---|
| `fn name(p: t = d) -> t { }` | ⚠️ P2 | 参数注解 ✅；返回类型 `-> type` 被「消费但忽略，运行时不强制」 |
| 箭头函数 `x => expr` | ✅ | ParseLambda |
| 箭头函数 `x => { }` | ✅ | 同上 |
| `begin/process/end` 块 modern 形式 | ✅ | ParseScriptBlockExpression |

### §4 字面量与访问

| 项 | 状态 | 证据 |
|---|---|---|
| `[1,2,3]` 数组 | ✅ | ModernParser LBracket 分支 |
| `{ k: v }` 哈希 | ❌ **P1 未实现** | `ParsePrimary` 中 `LBrace` 一律作脚本块，无哈希字面量解析 |
| `$` 简写（= `$_`） | ⚠️ P2 | `$` 单独 → `Variable("")`（空名），非 `Variable("_")` |
| `$.prop` | ✅ | Tokenizer `$.Name` → `Variable("_")` + `Dot` + `Name` |
| `?.` | ✅ | NullCondMember |
| `?[]` | ⚠️ P2 | null 条件信息在 AST 丢失（用普通 IndexExpression） |
| `??` | ✅ | |
| `? :` 三元 | ✅ | |
| `0..<10` 半开范围 | ❌ P1 | 无 `..<` token |
| `"""..."""` 多行字符串 | ✅ | LexTripleQuotedString |
| `r"..."` 原始字符串 | ✅ | LexRawString |
| `${expr}` 插值 | ⚠️ P2 | 仅 `${name}` + 成员/索引后缀，非任意表达式 |

### §5 控制流

| 项 | 状态 | 证据 |
|---|---|---|
| `if cond {}`（无括号） | ❌ **P1 未实现** | ParseIf 调 ParseParenExpression，强制 LParen |
| `elif` | ✅ | ParseIf `MatchKeyword("elif")` |
| `for x in col` | ❌ P1 | for/foreach 分离，仍用 `foreach ($x in $col)` |
| `for i in 0..<10` | ❌ P1 | 依赖半开范围 + for-in，二者均缺 |
| `for k, v in hash` | ❌ P1 | ParseForEach 仅读单 Variable，无解构 |
| `match x {}`（非 fall-through） | ✅ | 产 `MatchExpression` 独立节点（与 ADR 示例的 `SwitchStatementAst(NonFallThrough)` 形态不同，但功能等价） |
| `while c {}`/`do {} while c`（无括号） | ❌ P1 | 同 if，强制括号 |
| `try { } catch e: Type { }` | ❌ P1 | 用 PS 风格 `catch [Type] as $ex`，现代 `catch e: Type` 未实现 |
| `break label`（去冒号） | ❌ **P1 未实现** | `TokenKind.Label` 从未被 Tokenizer 产生，MatchLabel 永远返回 null |

### §6 字符串

| 项 | 状态 | 证据 |
|---|---|---|
| 单引号/双引号 | ✅ | LexString |
| `"""..."""` | ✅ | LexTripleQuotedString |
| `r"..."` | ✅ | LexRawString |
| 三引号缩进剥离 | ✅ | StripCommonIndent |

### §7 类型注解

| 项 | 状态 | 证据 |
|---|---|---|
| `name: type` 后缀形式 | ✅ | ParseModernParameterDeclarations |
| `@ValidateRange(0,100)` 特性 | ❌ P1 | 无 `@Attribute` 解析，AST 无 `AttributeAst` |
| `int[]` 数组类型（后缀形式） | ⚠️ P2 | 现代 `name: int[]` 经 ParseTypeReferenceTerm 不支持 `[]` 后缀 |
| 类型推导 | ⚠️ P2 | 类型可省略，但返回类型 `-> type` 被忽略 |

### §8 命令调用

| 项 | 状态 | 证据 |
|---|---|---|
| cmdlet 保持 Verb-Noun | ✅ | Tokenizer 允许标识符含 `-` |
| `cmd(name: value)` 关键字参数 | ❌ P1 | ParseArgumentList 全部产 PositionalArgument |

### §9 注释

| 项 | 状态 | 证据 |
|---|---|---|
| `#` 单行 | ✅ | LexLineComment |
| `<# #>` 多行 | ✅ | LexBlockComment |
| `"""` 文档注释（`DocumentationCommentAst`） | ❌ P1 | 全仓无 `DocumentationCommentAst`，`"""` 仅作字符串 |
| TODO/FIXME/HACK 标记 | ❌ P1 | 仅作普通 LineComment，无标记提取 |

### §10 互操作

| 项 | 状态 | 证据 |
|---|---|---|
| `import "file.osh"`/`import "file.ps1"` | ✅ | ParseImport → UsingStatement |
| 块切换互操作 `#lang ps1 { }` | ❌ P1 | 见 §1 |
| 自动变量互通 | ⚠️ P2 | `$_` 共享 ✅；`$.name`≡`$_.name` ✅；但 `$` 单独简写映射为空名变量 |

---

## 三、附加审计项

### 1. 现代保留字 `fn`/`match`/`elif`/`in`
- ⚠️ **P2 部分实现**：Tokenizer 标为 Keyword，间接阻止作命令名/函数名；但**未禁止作变量名**（`$fn = 5` 合法），违反 ADR §约束。

### 2. Parser 错误信息前缀
- ⚠️ **P2 不一致**：ModernParser 内联标注 `[modern]`；PowerShellParser 内联**无 `[ps1]` 前缀**；REPL 层统一包装导致 ModernParser 错误被双重前缀。

### 3. Tokenizer 模式区分
- ❌ **P0 设计偏差**：Tokenizer 不区分 modern/ps1 模式，两套运算符 token 并存由 parser 自选。这导致 `.ps1` 模式下现代 token 仍被产生并被 PowerShellParser 接受（见 §2 P0 项）。

### 4. 测试覆盖
- ❌ **P3 严重不足**：ModernParser 仅 4 个集成测试用例（三元、null 合并、lambda、逻辑运算符），无专用单元测试项目，无 `.osh` 端到端测试。未覆盖 `fn`、`match`、`[1,2,3]`、`?.`、`r""`、`"""`、`elif`、`for-in`、`try/catch`、`import`、`#lang` 等。

---

## 四、P0 项汇总（必须优先修复）

1. **PowerShellParser 接受现代运算符**（`:1279-1296`）——破坏 `.ps1` 兼容性承诺，PS 脚本中 `==` 会被误解析。
2. **Tokenizer 不区分模式**——是 P0-1 的根因，需引入模式感知。
3. **`break label` 完全失效**——Label token 从未产生，`break label`/`break :label` 均不工作。
4. **fn 返回类型 `-> type` 解析崩溃**（合规测试发现）——tokenizer 仅将 `=>` 识别为 `Arrow` token，`->` 被切分为 `Minus`+`Gt`，导致 `fn f() -> int { }` 抛 `[modern] expected '{', got Minus '-'`。审计原判「消费但忽略」有误，实际是崩溃。
5. **双引号字符串插值完全未实现**（合规测试发现）——`Tokenizer.LexString` 对双引号字符串中的 `$` 原样保留，`Evaluator` 对 `LiteralKind.String` 直接返回 `l.Value`，`"$var"`/`"$(expr)"`/`"""$var"""` 均不插值。全仓无 `ExpandableStringExpression`。此缺陷同时影响 PowerShell 兼容模式，属根因性缺陷。

> 完整修复任务清单见 `docs/modern-syntax-tasks.md`。

---

## 五、合规测试补充发现（2026-07-10）

建立 `ModernSyntaxComplianceTests` 时，对「已实现」特性的验证测试暴露了 4 个审计低估的缺陷：

| ID | 缺陷 | 严重度 | 证据 |
|----|------|--------|------|
| T-087 | fn 返回类型 `-> type` 崩溃 | P0 | `Tokenizer.cs:955` 仅 `=>` 产 `Arrow`；`ModernParser.cs:788` `Check(Arrow)` 永远 false |
| T-088 | 双引号字符串插值未实现 | P0 | `Tokenizer.cs:362` `$` 原样 append；`Evaluator.cs:792` `LiteralExpression` 直接返回 Value |
| T-089 | 三引号缩进剥离尾部空白 | P2 | `StripCommonIndent` 产出含尾部 `\n`，FluentAssertions 报「unexpected whitespace at the end」 |
| T-091 | 裸标识符算术 `a + b` 未实现 | P1 | ADR §7.2 要求无 `$` 前缀变量，但 `fn add(a,b) { a + b }` 抛 `[modern] unexpected token in expression: Plus '+'` |

---

## 六、2026-07-10 重新审查（PS 借鉴 T-100~T-113 完成后）

PS 借鉴主题（T-100~T-113）全部完成后，对现代语法实现做全面重新审查。本次审查逐项核对 ADR-0050 §1–§10 + 约束，给出精确文件:行号证据。

### 6.1 已完成项汇总（无需重复劳动）

| 任务 | 状态 | 证据 |
|------|------|------|
| T-087 fn `->` token 识别 | `[x]` | `Token.cs:107` RightArrow；`Tokenizer.cs:879` 识别 `->`；`ModernParser.cs:790-794` 消费 |
| T-088 双引号字符串插值 | `[x]` | `ExpandableStringParser.cs` 全文；`AstNodes.cs` ExpandableStringExpression；`Evaluator` EvaluateExpandableString |
| T-085 保留字禁作变量名 | `[x]` | `ModernParser.cs:2068` ModernReservedVariableNames（被 PS 借鉴 T-111 覆盖完成） |
| T-090 合规测试套件 | `[x]` | `ModernSyntaxComplianceTests.cs` 52 通过 / 33 跳过 / 0 失败 |
| PS 借鉴 T-100~T-113 | `[x]` | 见 `docs/ps-ref-reuse-tasks.md`（CharTraits/SourceSpan/ExpandableString/here-string/类型字面量/数字后缀/$(...)语句/语义检查） |

### 6.2 文档滞后修正

| 任务 | 原状态 | 实际状态 | 证据 |
|------|--------|----------|------|
| T-002 PowerShellParser 拒绝现代运算符 | `[ ]` | `[~]` 二元已移除，一元残留 | `PowerShellParser.cs:1256-1322` 二元已移除（注释 :1264/:1280）；`:1336` Bang 一元仍接受；`:1262` DoubleQuestion 仍接受 |

### 6.3 T-001 方向调整（PS 借鉴审计建议）

原 T-001「Tokenizer 引入 modern/ps1 LexMode」与 PS 借鉴审计 §四 冲突：
- PS 借鉴审计结论：**不引入全局 LexMode**，改为「tokenizer 产出带标记 token + parser 决策」。
- 理由：PS tokenizer 的 TokenizerMode 状态机强耦合 SMA，直接搬运破坏 OpenShell 架构；modern 语法的 `==`/`&&`/`=>` 等已由共享 Tokenizer 产生，PowerShellParser 只需在 parser 侧拒绝（T-002 工作面）。
- **T-001 调整为**：不引入 LexMode，工作合并到 T-002（parser 侧拒绝）+ T-012（块内不混用检查，基于 parser 侧拒绝）。

### 6.4 逐项重新审查结果

| # | 特性 | 状态 | 关键证据 | 对应任务 |
|---|------|------|----------|----------|
| 1 | Tokenizer LexMode 区分 | ❌ 调整方向 | `Tokenizer.cs:23-36` 无模式；PS 借鉴审计建议不引入 | T-001（调整） |
| 2 | PSParser 拒绝现代运算符 | ⚠️ 二元已做，一元残留 | `PowerShellParser.cs:1336` Bang；`:1262` DoubleQuestion | T-002（残留） |
| 3 | Label token + break label | ❌ | `Token.cs:120` 枚举有；Tokenizer 零产生；`ModernParser.cs:318-321` | T-003 |
| 4 | `#lang ps1 { }` 块切换 | ❌ | 全仓无 UnclosedLangBlock；仅 `Program.cs:1053` 行级 | T-010 |
| 5 | 无后缀默认现代 + ParseWarning | ❌ | `Evaluator.cs:155-157` 默认 PS；无 ParseWarning | T-011 |
| 6 | `~=` `~regex` `in` `contains` `++` 数组拼接 | ❌ | `ModernParser.cs:1657-1723` 均未列入 | T-020~T-024 |
| 7 | `.osh` PS 运算符 DeprecationWarning | ❌ | 全仓无 DeprecationWarning | T-025 |
| 8 | `{ k: v }` 哈希字面量 | ❌ | `ModernParser.cs:2027-2028` LBrace→ScriptBlock | T-030 |
| 9 | `$` 单独 → Variable("_") | ❌ | `Tokenizer.cs:773` → Variable("") 空名 | T-081 |
| 10 | `?[]` null 条件 AST 保留 | ❌ | `ModernParser.cs:1842-1844` 信息丢失；`AstNodes.cs:293-296` 无字段 | T-082 |
| 11 | `0..<10` 半开范围 | ❌ | `Tokenizer.cs:1100` 仅 `..`；无 `..<` | T-031 |
| 12 | `${expr}` 任意表达式插值 | ⚠️ | `ExpandableStringParser.cs:73-80` 仅 `${name}` | T-083 |
| 13 | `if`/`while`/`do` 无括号条件 | ❌ | `ModernParser.cs:533`/`:634`/`:649` 均 ParseParenExpression | T-040/T-041 |
| 14 | `for x in col` + `for k,v` | ❌ | `ModernParser.cs:674-690` foreach $x in $col 单变量 | T-042/T-043 |
| 15 | `catch e: Type` 现代绑定 | ❌ | `ModernParser.cs:706-719` PS `[Type] as $ex` | T-044 |
| 16 | `@ValidateRange` + AttributeAst | ❌ | `ModernParser.cs:2024` @ 仅 {/(；无 AttributeAst | T-050 |
| 17 | `cmd(name: value)` 关键字参数 | ❌ | `ModernParser.cs:1866-1880` 全 PositionalArgument | T-060 |
| 18 | `"""doc"""` DocumentationCommentAst | ❌ | `Tokenizer.cs:432` 仅字符串；无 DocumentationCommentAst | T-070 |
| 19 | fn `-> type` 运行时强制 | ❌ | `ModernParser.cs:790-794` SkipModernTypeReference 忽略 | T-080 |
| 20 | 裸标识符算术 `a + b` | ❌ | `ModernParser.cs:2030-2039` Identifier→CommandExpression | T-091 |
| 21 | `name: int[]` 后缀数组类型 | ❌ | `ModernParser.cs:880-899` 无 `[]` 分支 | T-084 |
| 22 | 三引号缩进剥离尾部空白 | ⚠️ | `Tokenizer.cs:496-509` 尾部 `\n` bug | T-089 |
| 23 | 错误前缀 `[modern]`/`[ps1]` | ⚠️ | `Program.cs:1148` REPL 双重 `[modern]`；PSParser 无内联 `[ps1]` | T-086 |

### 6.5 新发现缺陷

| 缺陷 | 严重度 | 证据 | 处理 |
|------|--------|------|------|
| `?[]` 信息丢失（审查表 #10） | P2 隐蔽 | `ModernParser.cs:1836` 读取 nullCond 后从未使用；`AstNodes.cs:293-296` IndexExpression 无 NullConditional 字段 | 合并到 T-082 |
| ~~T-002 残留 Bang 一元 + DoubleQuestion~~ | ~~P0~~ | ~~`PowerShellParser.cs:1336`、`:1262`~~ | **已修复（2026-07-10）**：Bang/DoubleQuestion/Question/NullCondMember/NullCondIndex 全部移除 + ParseScript 新增 IsModernOperatorToken 显式拒绝 |
| PowerShellParser 无内联 `[ps1]` 前缀 | P2 | 全文件零命中 | 合并到 T-086 |

### 6.6 P0 阻断项汇总（重新审查后）

1. ~~**T-002 残留**：PowerShellParser 仍接受 `!`（Bang 一元）+ `??`（DoubleQuestion）—— 破坏 `.ps1` 兼容性承诺。~~ **已修复（2026-07-10）**。
2. **T-003**：break/continue label 完全失效（Label token 从未产生）。**← 当前唯一 P0 阻断项**

> 注：T-001（LexMode）调整方向后不再属 P0，工作合并到 T-002 + T-012。T-087/T-088（原 P0 崩溃项）已完成。
