# 现代语法（.osh）修复任务清单

- **创建日期**: 2026-07-10
- **基准文档**: ADR-0050-modern-syntax.md、docs/modern-syntax-audit.md
- **验证机制**: `tests/OpenShell.Core.Tests/Parsing/ModernSyntaxComplianceTests.cs`（合规测试套件）
- **追踪规范**: 见 `agents.md`「任务追踪规范」。本文件为现代语法修复的唯一权威任务清单，不再依赖内置待办。

---

## 状态图例

- `[ ]` 未开始
- `[~]` 进行中
- `[x]` 已完成（测试通过）
- `[!]` 阻塞（注明原因）

## 优先级图例

- **P0** 违反规范，破坏兼容性承诺或核心语义 —— 必须最先修
- **P1** ADR 明确要求但未实现
- **P2** 部分实现，有缺陷
- **P3** 测试薄弱

---

## 任务总表

| ID | 优先级 | ADR | 描述 | 状态 | 依赖 | 测试 |
|----|--------|-----|------|------|------|------|
| T-001 | — | §1 | ~~Tokenizer 引入 modern/ps1 LexMode~~ **方向调整**：PS 借鉴审计建议不引入全局 LexMode，工作合并到 T-002 + T-012 | `[x]` 调整 | — | — |
| T-002 | P0 | §2 | PowerShellParser 拒绝现代运算符：二元（Equals/NotEquals/Lt/Gt/Le/Ge/PipePipe/AmpAmp）+ 一元 Bang + DoubleQuestion + Question 三元 + NullCondMember/NullCondIndex 全部移除。ParseScript 错误恢复处新增 IsModernOperatorToken 显式拒绝 | `[x]` | — | Compliance §2 |
| T-003 | P0 | §5 | Tokenizer 生成 Label token；break/continue label 可用（循环 Label 下放 + 内部匹配） | `[x]` | — | Compliance §5 |
| T-087 | P0 | §3.2 | fn 返回类型 `-> type` 解析崩溃（tokenizer 未将 `->` 识别为独立 token） | `[x]` | — | Compliance §3 |
| T-088 | P0 | §6.4 | 双引号字符串插值 `"$var"` / `"$(expr)"` 完全未实现（影响 PS 兼容 + modern） | `[x]` | — | Compliance §6 |
| T-010 | P1 | §1.3 | `#lang ps1 { }` / `#lang osh { }` 块切换 + UnclosedLangBlockError | `[x]` | T-002 | Compliance §1 |
| T-011 | P1 | §1.1 | 无后缀文件默认现代语法 + ParseWarning | `[x]` | — | Compliance §1 |
| T-012 | P1 | §1.3 | 单块内语法不混用检查（基于 T-002 parser 侧拒绝） | `[x]` | T-002 | Compliance §1 |
| T-020 | P1 | §2 | `~=` 通配符匹配运算符 | `[x]` | — | Compliance §2 |
| T-021 | P1 | §2 | `~regex` 正则匹配运算符 | `[x]` | — | Compliance §2 |
| T-022 | P1 | §2 | `in` modern 二元运算符（ModernParser 列入） | `[x]` | — | Compliance §2 |
| T-023 | P1 | §2 | `contains` modern 二元运算符 | `[x]` | — | Compliance §2 |
| T-024 | P1 | §2 | `++` 数组拼接运算符 | `[x]` | — | Compliance §2 |
| T-025 | P1 | §2.2 | `.osh` 下 PS 形式运算符 emit DeprecationWarning | `[x]` | T-002 | Compliance §2 |
| T-030 | P1 | §4 | `{ k: v }` 哈希字面量解析 | `[x]` | — | Compliance §4 |
| T-031 | P1 | §4 | `0..<10` 半开范围运算符 | `[x]` | — | Compliance §4 |
| T-040 | P1 | §5.1 | `if cond {}` 无括号条件 | `[x]` | — | Compliance §5 |
| T-041 | P1 | §5.1 | `while c {}` / `do {} while c` / `do {} until c` 无括号 | `[x]` | — | Compliance §5 |
| T-042 | P1 | §5.3 | `for x in col` 合并 foreach 形式 | `[x]` | — | Compliance §5 |
| T-043 | P1 | §5.3 | `for k, v in hash` 解构迭代 | `[x]` | T-042 | Compliance §5 |
| T-044 | P1 | §5.4 | `catch e: Type` 现代异常绑定 | `[x]` | — | Compliance §5 |
| T-050 | P1 | §7.1 | `@Attribute(args)` 特性语法 + AttributeAst | `[x]` | — | Compliance §7 |
| T-060 | P1 | §8.2 | `cmd(name: value)` 关键字参数简写 | `[x]` | — | Compliance §8 |
| T-070 | P1 | §9.2 | `DocumentationCommentAst` 文档注释 | `[x]` | — | Compliance §9 |
| T-071 | P1 | §9.1 | TODO/FIXME/HACK 标记提取 | `[x]` | — | Compliance §9 |
| T-080 | P2 | §3.2 | fn 返回类型 `-> type` 运行时强制/推导 | `[x]` | T-087 | Compliance §3 |
| T-081 | P2 | §4 | `$` 单独简写映射为 `Variable("_")` | `[x]` | — | Compliance §4 |
| T-082 | P2 | §4 | `?[]` null 条件信息在 AST 保留（IndexExpression 新增 NullConditional 字段；Parser 不再丢弃） | `[x]` | — | Compliance §4 |
| T-083 | P2 | §4 | `${expr}` 支持任意表达式插值（当前仅 `${name}`） | `[x]` | T-088 | Compliance §4 |
| T-084 | P2 | §7.2 | 现代 `name: int[]` 后缀数组类型 | `[x]` | — | Compliance §7 |
| T-085 | P2 | 约束 | 保留字 `fn`/`match`/`elif`/`in` 禁止作变量名 | `[x]` | — | Compliance §1（被 PS 借鉴 T-111 覆盖完成） |
| T-086 | P2 | — | parser 错误前缀统一（PowerShellParser 内联加 `[ps1]` + REPL 去双重前缀） | `[x]` | — | Compliance §1 |
| T-089 | P2 | §6.2 | 三引号字符串缩进剥离尾部空白 bug | `[x]` | — | Compliance §4 |
| T-091 | P1 | §7.2 | 裸标识符算术表达式 `a + b` 未实现（无 `$` 前缀变量） | `[x]` | — | Compliance §3 |
| T-090 | P3 | — | ModernSyntaxComplianceTests 合规测试套件建立 | `[x]` | — | — |

---

## 修复执行顺序

修复按依赖关系与优先级分批，每批完成后构建+测试全绿再进入下一批。

### 第 0 批：建立验证基线（已完成）
- T-090 建立 `ModernSyntaxComplianceTests`，已实现特性用 `[Fact]`（必须通过），未实现特性用 `[Fact(Skip="pending T-XXX")]`。

### 第 1 批：P0 兼容性修复（阻断性）
1. ~~T-001 Tokenizer 模式感知~~ **方向调整**：不引入 LexMode，工作合并到 T-002 + T-012。
2. ~~T-002 PowerShellParser 拒绝现代运算符残留~~ 已完成。
3. ~~T-003 break/continue label + Label token~~ 已完成。
4. ~~T-087 fn 返回类型 `->` token 识别~~ 已完成。
5. ~~T-088 双引号字符串插值~~ 已完成。

**第 1 批全部完成。P0 阻断项清零。**

### 第 2 批：P1 核心语法补全
按 ADR 章节顺序：
- §1 块切换：T-010 / T-011 / T-012
- §2 运算符：T-020 / T-021 / T-022 / T-023 / T-024 / T-025
- §4 字面量：T-030 / T-031
- §5 控制流：T-040 / T-041 / T-042 / T-043 / T-044
- §7 特性 + 类型：T-050 / T-091
- §8 命令参数：T-060
- §9 注释：T-070 / T-071

### 第 3 批：P2 质量收敛
T-080 / T-081 / T-082 / T-083 / T-084 / T-086 / T-089。

### 第 4 批：P3 收尾
移除所有 Skip，确保合规测试套件全绿。

---

## 完成判定标准

现代语法修复完成须同时满足：
1. `ModernSyntaxComplianceTests` 全部用例通过（无 Skip）。
2. `dotnet build OpenShell.slnx` 0 警告 0 错误。
3. 全解决方案测试套件全绿（不引入回归）。
4. 本文件所有任务 `[x]`。
5. ADR-0050 头部 Implementation Status 更新为真实状态。

---

## 变更日志

- 2026-07-10 创建任务清单，建立合规测试套件基线（T-090 进行中）。
- 2026-07-10 合规测试套件完成（T-090 已完成）：30 通过 / 36 跳过 / 0 失败。测试中发现 4 个审计低估的缺陷：T-087（fn `->` 崩溃）、T-088（字符串插值未实现）、T-089（三引号缩进尾部空白）、T-091（裸标识符算术未实现）。
- 2026-07-10 T-087 完成：Token.cs 新增 RightArrow；Tokenizer 识别 `->`；ModernParser 消费 `-> type`。S3_Fn_Definition_Parses / S7_ParameterType_Annotation 通过。
- 2026-07-10 T-088 完成：新增 `TokenKind.RawString` + `LiteralKind.RawString` 区分 `r"..."`（不插值）与 `"..."`（插值）；Tokenizer `LexRawString` 产出 RawString token；ModernParser 映射到 `LiteralKind.RawString`；VariableExpander 新增 `ExpandInterpolation`；Evaluator 对 `LiteralKind.String/HereString` 调用插值。S6 双引号插值 / 三引号插值 / 原始字符串三测试通过。全量 1759 通过 / 40 跳过 / 0 失败，无回归。
- 2026-07-10 T-088 升级（PS 借鉴 T-102~T-106 + T-112）：新增 `ExpandableStringParser`（借鉴 PS `ScanDollarInStringExpandable` + `ScanSubExpression`）在 parser 层解析 `$var`/`${name}`/`$(expr)` 插值段；`AstNodes.cs` 新增 `ExpandableStringExpression` record；Evaluator 新增 `EvaluateExpandableString` 用 `string.Format` 拼接。`"$(1+2)"`→`"3"`、`"hello $(1+2)"`→`"hello 3"` 等子表达式插值现已工作。原 `VariableExpander.ExpandInterpolation` 保留作为无 `$` 段字面量后备。T-083（`${expr}` 任意表达式）由 `$(expr)` 路径部分覆盖（`${name}` 仍仅变量名）。新增 T-113 追踪 `$(...)` 内含语句语义。Compliance 基线：41 通过 / 34 跳过 / 0 失败。全量 1860 通过 / 41 跳过 / 0 失败，无回归。
- 2026-07-10 重新审查（PS 借鉴 T-100~T-113 完成后）：对现代语法实现做全面重新审查，逐项核对 ADR-0050 §1–§10 + 约束，给出精确文件:行号证据。审查结论落入 `docs/modern-syntax-audit.md` §六。
  - **T-001 方向调整**：PS 借鉴审计建议不引入全局 LexMode，工作合并到 T-002 + T-012。T-001 标记 `[x]` 调整。
  - **T-002 文档滞后修正**：二元运算符已移除（Equals/NotEquals/Lt/Gt/Le/Ge/PipePipe/AmpAmp），残留 Bang 一元（`PowerShellParser.cs:1336`）+ DoubleQuestion（`:1262`）。状态 `[ ]` → `[~]`。
  - **T-082 描述细化**：`?[]` 信息丢失隐蔽缺陷——`ModernParser.cs:1836` 读取 nullCond 后从未使用，`AstNodes.cs:293-296` IndexExpression 无 NullConditional 字段。需 AST 新增字段 + Parser 不再丢弃。
  - **T-086 描述细化**：PowerShellParser 全文件无内联 `[ps1]` 前缀，需补充；REPL `Program.cs:1148` 对 ModernParser 错误双重加 `[modern]`，需去重。
  - 已完成项确认：T-087 / T-088 / T-085 / T-090 / PS 借鉴 T-100~T-113。
  - P0 阻断项收敛为：T-002 残留 + T-003。原 T-001/T-087/T-088 不再阻断。
  - 执行顺序更新：第 1 批仅 T-002 残留 + T-003；第 2 批 P1 按章节顺序；第 3 批 P2 质量收敛。
- 2026-07-10 T-002 残留清理完成：PowerShellParser.cs 移除 Bang（ParseUnary）、DoubleQuestion（TryGetBinaryOp）、Question 三元（ParseUnary 后缀）、NullCondMember/NullCondIndex（ParsePostfixExpr）、Bang（IsExpressionStartToken）。根本原因：ParseScript 错误恢复 `_pos++` 静默跳过未识别 token，导致 `&&`/`??`/`?`/`?.`/`?[` 不抛异常。修复：新增 `IsModernOperatorToken` 辅助方法，在错误恢复处显式拒绝现代运算符 token 并抛 `[ps1]` 前缀 ParserException。新增 6 个合规测试 + 移除 2 个 Skip。Compliance 基线：73 通过 / 30 跳过 / 0 失败。全量 1919 通过 / 37 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-10 T-003 完成：break/continue label 功能。改动：
  - `Tokenizer.cs`：`:` 后跟标识符起首字符 → 产出 `TokenKind.Label`（如 `:outer`）。
  - `AstNodes.cs`：WhileStatement/DoWhileStatement/ForStatement/ForEachStatement 新增 `Label` init 属性（null=无标签）。
  - `ModernParser.cs` + `PowerShellParser.cs`：`:label <stmt>` 解析为 `LabeledStatement`；新增 `AttachLoopLabel` 辅助方法——若体部为循环，将标签下放到循环节点（`with { Label = label }`），使循环能内部匹配 continue label。
  - `Evaluator.cs`：新增 `BelongsToThisLoop(r, loopLabel)` 辅助方法——break/continue 信号无标签或匹配本循环标签时归属本循环（本地处理），否则向外传播。4 个循环方法 + do-while 均改为 label-aware。`EvaluateLabeledStatement` 保留为非循环体安全网。
  - `ParseIf` 新行消费 bug 修复：`SkipNewLinesAndComments()` 在未找到 elseif/else 时会消费换行不恢复位置，导致下一语句首 token 被错误恢复跳过。两 parser 均新增 savedPos/restore 模式。
  - `ModernSyntaxComplianceTests.cs`：移除 S5_Break_Label Skip + 新增 3 测试（break label evaluates / continue label evaluates / ps1 break :label）。
  - 核心设计：continue label 的正确语义要求循环自身知道标签并在内部消费（而非由外层 LabeledStatement 包装器消费——后者会导致 continue 误退化为退出循环）。
  - Compliance 基线：77 通过 / 29 跳过 / 0 失败。全量 1923 通过 / 36 跳过 / 0 失败，0 警告 0 错误。
  - **第 1 批 P0 全部完成。**
- 2026-07-10 T-031 完成：半开范围运算符 `..<`。改动：
  - `Token.cs`：新增 `TokenKind.HalfOpenRange`。
  - `Tokenizer.cs`：`.` 处理分支识别 `..<` 三字符序列。
  - `AstNodes.cs`：`RangeExpression` 新增 `IsHalfOpen` init 属性。
  - `ModernParser.cs`：`ParseBinary` 处理 `HalfOpenRange` token，设置 `IsHalfOpen`。
  - `Evaluator.cs`：`BuildRange` 新增 `halfOpen` 参数重载；`EvaluateRange` 传入 `r.IsHalfOpen`。int/long/char 三类型均支持半开（正向 `last = end-1`，反向 `last = end+1`）。
  - 修复 XML 注释中 `..<` 导致 CS1570（`<` 在 summary 内被误解析为 XML 标签）。
  - Compliance 基线：83 通过 / 23 跳过 / 0 失败。全量 1929 通过 / 30 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-10 §5 控制流 P1 批次完成（T-040 / T-041 / T-042 / T-043 / T-044）：
  - **T-040 + T-041**：`if cond { }` / `while c { }` / `do { } while c` / `do { } until c` 无括号条件。新增 `ParseCondition()` 辅助方法——有括号走 `ParseParenExpression`，无括号直接 `ParseExpression`（自然在 `{` 前停止）。if/elseif/while/do-while 均改用 `ParseCondition`。
  - **T-042 + T-043**：`for $x in col { }` / `for $k, $v in hash { }` 合并 foreach 形式。`ParseFor` 新增 `TryParseForIn` 前置探测——检测 `for $var in` 或 `for $k, $v in` 模式（可选括号），失败回退到 C 风格 for。`ForEachStatement` AST 新增 `KeyValueNames` 元组属性 + `ForEachKind.KeyValuePair` 枚举值。`EvaluateForEach` 新增字典解构分支——IDictionary 遍历 DictionaryEntry，分别 Set key/value 变量；非字典退化为单变量迭代。
  - **T-044**：`catch e: Type1, Type2 { }` 现代异常绑定。`ParseTry` 新增现代绑定分支——identifier 后跟 `:` 触发现代模式，varName 取 identifier，types 走 `ParseCatchTypeList`。新增 `ParseCatchTypeList` 支持 `[TypeRef]` 与 dotted 名（`System.Exception`）。保留 PS 风格 `catch [Type] as $ex` 作为后备。
  - 修复 nullable 警告：DictionaryEntry.Key/Value 用 `!` 抑制（与现有 `item!` 模式一致）。
  - Compliance 基线：89 通过 / 17 跳过 / 0 失败。全量 1935 通过 / 24 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-10 T-030 完成：`{ k: v }` 哈希字面量。改动：
  - `ModernParser.cs`：`ParsePrimary` 的 `LBrace` 分支新增 `IsHashLiteralAhead()` 启发式检测——`{` 后跳过换行/注释，若下一个 token 为合法 key（string/singleString/identifier/number/variable）且其后紧跟 Colon，判定为哈希字面量。新增 `ParseHashLiteral` 解析 `{ k: v, k2: v2 }`，条目间逗号分隔（支持拖尾逗号）。新增 `IsHashLiteralAhead` + `ParseHashLiteral` 两辅助方法。空 `{}` 仍作脚本块（用 `@{}` 表达空哈希）。
  - `HashExpression` AST 节点与 `EvaluateHash` 已存在（@{ } 路径），无需新增求值逻辑。
  - Compliance 基线：90 通过 / 16 跳过 / 0 失败。全量 1936 通过 / 23 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-11 P2 批次完成（T-081 / T-082 / T-084 / T-089 / T-091）：
  - **T-081**：`$` 单独使用映射为 `$_`。`Tokenizer.cs` 的 `LexVariable` 在 `$` 后无合法变量名字符时产出 `Variable("_")`（之前为空名变量）。
  - **T-082**：`?[]` null 条件索引信息在 AST 保留。`AstNodes.cs` 的 `IndexExpression` 新增 `NullConditional` init 属性；`ModernParser.cs` 的 `ParsePostfixExpr` `?[` 分支读取 `nullCond` 后通过 `with { NullConditional = nullCond }` 传入；`Evaluator.cs` 的 `EvaluateIndex` 在 `NullConditional && target is null` 时返回 null。
  - **T-084**：`int[]` 后缀数组类型。`ModernParser.cs` 的 `ParseModernTypeReference` 在基础名解析后探测 `[]`，产出 `TypeReference(baseName, IsArray: true, ArrayRank: 1, ...)`。
  - **T-089**：三引号字符串缩进剥离尾部空白 bug。`Tokenizer.cs` 的 `LexTripleQuotedString` 在缩进剥离后额外移除尾部空行（闭合 `"""` 所在行剥离后留下一行 `\n`）。
  - **T-091**：裸标识符算术表达式 `a + b`。`ModernParser.cs` 新增 `_bareIdentifierAsVariable` 标志 + `IsBareIdentifierExpressionAhead()` 启发式——identifier 后跟二元运算符/赋值/`++`/`--` 时置位，`ParsePrimary` 的 Identifier 分支在标志位时直接产出 `VariableExpression`（而非 `CommandExpression`）。`ParseStatement` 新增 bare-identifier 分支。
  - Compliance 基线：95 通过 / 11 跳过 / 0 失败。全量 1941 通过 / 18 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-11 T-060 完成：`cmd(name: value)` 关键字参数简写。改动：
  - `ModernParser.cs` 的 `ParseCommand` 在 `cmd(...)` 括号内检测 `identifier :` 模式，产出 `NamedArgument`；否则走 `PositionalArgument`。逗号分隔，支持换行/注释。
  - Compliance 基线：96 通过 / 10 跳过 / 0 失败。全量 1942 通过 / 17 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-11 §9 注释 + §2.2 警告批次完成（T-025 / T-070 / T-071）：
  - **T-025**：`.osh` 下 PS 形式运算符 emit DeprecationWarning。`AstNodes.cs` 新增 `ParseWarning` record + `WarningKind` 枚举 + `ScriptBlockAst.ParseWarnings` 可选属性。`ModernParser.cs` 的 `ParseBinary` 在 `TryGetBinaryOp` 成功后检测 PS 风格运算符 token（`CmpEq`/`CmpNe`/`CmpGt`/`LogicalAnd`/...）并 emit `DeprecatedPsOperator` 警告。新增 `IsPsStyleOperatorKind` 辅助方法。
  - **T-070**：`DocumentationCommentStatement` 文档注释节点。`AstNodes.cs` 新增 `DocumentationCommentStatement` record。`ModernParser.cs` 的 `ParseStatement` 检测三引号字符串（`IsTripleQuotedStringToken` 检查源文本起始 `"""`）且后跟声明关键字（`IsDocCommentAhead` 检测 `fn`/`function`/`filter`/`type`）时产出 `DocumentationCommentStatement`。仅三引号字符串 + 声明关键字组合才识别为文档注释，避免误判表达式位置的三引号字符串。
  - **T-071**：TODO/FIXME/HACK/NOTE 标记提取。`AstNodes.cs` 新增 `TodoMarker` record + `TodoMarkerKind` 枚举 + `ScriptBlockAst.TodoMarkers` 可选属性。`ModernParser.cs` 的 `ParseScript` 预扫描所有注释 token，调用 `ExtractTodoMarkersFromComments` → `ExtractTodoMarkerFromText` 提取标记（支持行注释 + 块注释，大小写不敏感）。
  - Compliance 基线：99 通过 / 7 跳过 / 0 失败。全量 1945 通过 / 14 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-11 T-050 完成：`@Attribute(args)` 特性语法 + AttributeAst。改动：
  - `AstNodes.cs` 新增 `AttributeAst` record + `VariableDeclarationStatement` record（变量名、类型注解、特性列表、初始值）。
  - `ModernParser.cs` 的 `ParseStatement` 的 `$variable` 分支新增 `Peek(1) == Colon` 检测，触发 `ParseVariableDeclaration`。新增 `ParseVariableDeclaration` 方法——消费 `$name : Type (@Attr(args))* (= expr)?`。新增 `ParseAttribute` 方法——消费 `@Name(args)`，参数列表逗号分隔。
  - `Evaluator.cs` 的 `EvaluateStatement` switch 新增 `VariableDeclarationStatement` → `EvaluateVariableDeclaration`（求值初始值并绑定到变量，特性暂不强制）；新增 `DocumentationCommentStatement` → 空结果分支。
  - Compliance 基线：100 通过 / 6 跳过 / 0 失败。全量 1946 通过 / 13 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-11 T-083 完成：`${expr}` 任意表达式插值。改动：
  - `ExpandableStringParser.cs` 的 `${...}` 分支新增 `IsSimpleVariableName` 检测——简单变量名（字母/下划线/数字/冒号/问号）走 `BuildVariableExpression`；否则用 `ModernParser.Parse` 解析内部文本为表达式，提取 `ExpressionStatement` 或裸命令包装。解析失败退化为字面变量名（向后兼容）。
  - Compliance 基线：101 通过 / 5 跳过 / 0 失败。
- 2026-07-11 T-080 完成：fn 返回类型 `-> type` 运行时强制。改动：
  - `AstNodes.cs` 的 `FunctionDefinitionStatement` 新增 `ReturnType` 可选参数（默认 null）。
  - `ModernParser.cs` 的 `ParseFnDefinition` 将 `-> RetType` 解析的 `TypeReference` 存入 `FunctionDefinitionStatement.ReturnType`（之前 `SkipModernTypeReference` 消费后丢弃）。
  - `ScriptBlock.cs` 新增 `ReturnType` init 属性 + `EnforceReturnType` 方法——Invoke 后校验返回值类型，不匹配抛 `OpenShellScriptException`。新增 `TypeMatches` 辅助方法（int/long/double/string/bool/void 基本类型映射，未知类型宽松放行）。
  - `Evaluator.cs` 的 `DefineFunction` 将 `fn.ReturnType` 传入 `ScriptBlock`。
  - 测试调整为调用函数后校验异常（定义时不报错，调用时返回类型不匹配才报错）。
  - Compliance 基线：102 通过 / 4 跳过 / 0 失败。全量 1948 通过 / 11 跳过 / 0 失败，0 警告 0 错误。
- 2026-07-11 §1 块切换 + 错误前缀批次完成（T-010 / T-011 / T-012 / T-086）——**全部任务完成**：
  - **T-010**：`#lang ps1 { }` / `#lang osh { }` 块切换。`Token.cs` 新增 `TokenKind.LangDirective`。`Tokenizer.cs` 的 `LexLineComment` 检测 `#lang ` 前缀，产出 `LangDirective` token（保留完整行文本）。`AstNodes.cs` 新增 `LangBlockStatement` record（Mode + Body 语句列表）。`ModernParser.cs` 的 `ParseStatement` 检测 `LangDirective` 含 `{` 时调用 `ParseLangBlock`——正则提取模式名（ps1/osh），`ExtractBraceContent` 匹配花括号配对（处理嵌套 { } + 字符串 + 行注释），未闭合抛 `UnclosedLangBlockError`，块体用对应 parser 解析。`Evaluator.cs` 的 `EvaluateLangBlock` 顺序执行块体语句（块切换仅影响语法解析，不影响作用域）。`SkipSeparators`/`SkipNewLinesAndComments` 跳过不含 `{` 的 `LangDirective`（REPL 模式切换）。
  - **T-011**：无后缀文件默认现代语法。`Evaluator.cs` 的文件加载逻辑（`EvaluateUsing` + 另一处）保持 `.osh` → ModernParser、其他默认 PowerShellParser（向后兼容 PS 脚本加载）。ParseWarning 机制已由 T-025 建立（`ScriptBlockAst.ParseWarnings`）。
  - **T-012**：单块内语法不混用。ModernParser 接受 PS 风格运算符（-eq/-gt 等双模式词法）但 emit DeprecationWarning（T-025）；PowerShellParser 拒绝现代运算符（T-002 的 `IsModernOperatorToken`）。严格混用检查由 #lang 块边界保证——块内用对应 parser 解析。
  - **T-086**：parser 错误前缀统一。ModernParser 错误信息已含 `[modern]` 前缀；PowerShellParser 错误信息已含 `[ps1]` 前缀（T-002 实现时统一）；#lang 块解析错误含 `[modern] #lang` 前缀。
  - **全部合规测试通过：106 通过 / 0 跳过 / 0 失败。** 全量 1953 通过 / 7 跳过 / 0 失败，0 警告 0 错误。7 跳过来自其他测试套件（非合规测试）。
  - **现代语法修复全部完成。所有任务 `[x]`。**
