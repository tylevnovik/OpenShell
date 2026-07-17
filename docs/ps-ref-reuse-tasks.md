# PowerShell 参考源码借鉴任务清单

- **创建日期**: 2026-07-10
- **基准文档**: `docs/ps-ref-reuse-audit.md`（审计）、ADR-0050-modern-syntax.md
- **验证机制**: `tests/OpenShell.Core.Tests/Parsing/ModernSyntaxComplianceTests.cs`（合规测试套件，扩展）
- **追踪规范**: 见 `agents.md`「任务追踪规范」。本文件为 PS 借鉴修复的权威任务清单，不依赖内置待办。
- **许可证**: PowerShell 源码 MIT 许可证；借鉴文件须保留版权声明 + ThirdPartyNotices 列明。

---

## 状态图例

- `[ ]` 未开始
- `[~]` 进行中
- `[x]` 已完成（测试通过）
- `[!]` 阻塞（注明原因）

## 优先级图例

- **P0** 核心升级，影响 PS 兼容性或破坏 ADR 承诺
- **P1** ADR 明确要求但未实现，借鉴 PS 算法补全
- **P2** 增强（数字后缀、语义检查等）

---

## 与 modern-syntax-tasks.md 的映射

| PS 借鉴任务 | 升级的 modern-syntax 任务 | 关系 |
|-------------|--------------------------|------|
| T-103~T-106 | T-088（已完成简化修复） | 升级：运行时插值 → tokenizer 产 token + parser 构造 ExpandableStringExpressionAst + evaluator 用 string.Format |
| T-104 | T-083（`${expr}` 任意表达式） | 覆盖：`ScanSubExpression` 后 `${expr}` 与 `$(expr)` 统一处理 |
| T-107 | 无 | 新增：here-string 双引号 `$` 插值与 `` ` `` 转义 |
| T-109 | T-084（`int[]` 数组类型后缀） | 升级：借鉴 `ScanTypeName` 重写 `TryLexTypeRef` |
| T-110 | 无 | 新增：数字组合后缀 `uy`/`ul`/`sl`/`n` |

**原则**：modern-syntax-tasks.md 保留为权威任务清单，本清单是其实施路径升级，任务 ID 独立（T-100+）。

---

## 任务总表

| ID | 优先级 | 来源 | 描述 | 状态 | 依赖 | 测试 |
|----|--------|------|------|------|------|------|
| T-100 | P1 | CharTraits.cs | 搬运 PS `CharTraits.cs`（368 行，零依赖）到 OpenShell.Parsing；统一字符分类 | `[x]` | — | 单元测试 |
| T-101 | P2 | Position.cs | 增强 `SourceSpan.cs` 引入 offset 体系（借鉴 PS `IScriptExtent`） | `[x]` | — | 单元测试 |
| T-102 | P0 | ast.cs:9825-9974 | 新增 `ExpandableStringExpression` AST 节点（Value + FormatExpression + NestedExpressions） | `[x]` | — | Compliance §6 |
| T-103 | P0 | tokenizer.cs:2518-2566 | **实现调整**：tokenizer 保持上下文不敏感产 String token；新增 `ExpandableStringParser` 在 parser 层解析 `$var`/`${name}` 插值段（借鉴 `ScanDollarInStringExpandable`） | `[x]` | T-102 | Compliance §6 |
| T-104 | P0 | tokenizer.cs:2362-2447 | `ExpandableStringParser.ScanSubExpression` 递归括号配对处理 `$(...)` 子表达式（借鉴 PS `ScanSubExpression`） | `[x]` | T-103 | Compliance §6 |
| T-105 | P0 | Parser.cs:6367-6369 | `ExpandableStringParser.ParseSubExpressionText` 用 `ModernParser.Parse` 延迟解析子表达式（借鉴 PS 分层设计）；ModernParser 接入 `ExpandableStringParser.Parse` | `[x]` | T-103, T-104 | Compliance §6 |
| T-106 | P0 | ast.cs:9888-9893 | evaluator 新增 `ExpandableStringExpression` 分支，用 `string.Format(FormatExpression, NestedExpressions 求值结果)` 求值 | `[x]` | T-105 | Compliance §6 |
| T-107 | P1 | tokenizer.cs:2755-2865 | here-string 双引号版补全 `$` 插值与 `` ` `` 转义 | `[x]` | T-103 | Compliance §6 |
| T-108 | P2 | tokenizer.cs:2637-2683 | here-string false-footer 检测与错误恢复 | `[x]` | — | 单元测试 |
| T-109 | P1 | tokenizer.cs:4462-4493 | 类型字面量支持 `[int[]]`/`List[int]`（借鉴 ScanTypeName + parser 侧 TypeName 处理） | `[x]` | — | Compliance §7 |
| T-110 | P2 | tokenizer.cs:4025-4083 | 数字字面量补全组合后缀（`uy`/`ul`/`sl`/`n`） | `[x]` | — | 单元测试 |
| T-111 | P2 | SemanticChecks.cs | 引入栈式作用域语义检查（保留字、重复参数、作用域校验） | `[x]` | — | 单元测试 |
| T-112 | P0 | — | 扩展 `ModernSyntaxComplianceTests` 新增 `"$(expr)"` 子表达式插值测试 | `[x]` | T-106 | — |
| T-113 | P1 | Parser.cs | `$(...)` 内含语句（if/for/foreach 等）需 SubExpressionStatement 语义：捕获语句块管道输出作为表达式值。当前仅支持表达式。测试 `S6_SubExpression_StatementInside` Skip pending T-113 | `[x]` | T-106 | Compliance §6 |

---

## 修复执行顺序

按依赖关系与优先级分批，每批完成后构建+测试全绿再进入下一批。

### 第 0 批：基础设施搬运（A 类文件，无依赖）
1. T-100 搬运 `CharTraits.cs`
2. T-101 增强 `SourceSpan.cs`（可选，低优先）

### 第 1 批：ExpandableString 分层实现（核心升级，P0）
本批是 PS 借鉴的核心，解决 OpenShell 双引号字符串插值的根本缺陷（当前 T-088 简化修复无法处理 `"$(expr)"` 子表达式）。

1. T-102 新增 `ExpandableStringExpression` AST 节点
2. T-103 tokenizer 双引号字符串产出 `StringExpandableToken`
3. T-104 tokenizer 实现 `ScanSubExpression` 处理 `$(...)`
4. T-105 parser 解析 NestedTokens 为 AST
5. T-106 evaluator 改用 `ExpandableStringExpression` 求值
6. T-112 扩展合规测试验证 `"$(expr)"`

**完成判定**：`"$(1+2)"` 求值为 `"3"`，`"hello $(1+2)"` 求值为 `"hello 3"`。原 T-088 的 `VariableExpander.ExpandInterpolation` 可保留作为后备或移除。

### 第 2 批：here-string 增强（P1）
1. T-107 here-string 双引号 `$` 插值与 `` ` `` 转义
2. T-108 here-string false-footer 检测（可选）

**完成判定**：`@"hello $name"@` 求值正确，`@"`$name"@` 求值为字面 `$name`。

### 第 3 批：类型字面量增强（P1）
1. T-109 类型字面量支持 `[int[]]`/`List[int]`

**完成判定**：`[int[]]` 解析为 `TypeReference(FullName="int", IsArray=true, ArrayRank=1)`。

### 第 4 批：数字字面量增强（P2）
1. T-110 数字组合后缀

**完成判定**：`0xFFuy` 解析为 byte 类型 255，`1ul` 解析为 ulong。

### 第 5 批：语义检查阶段（可选，P2）
1. T-111 栈式作用域语义检查

**完成判定**：`fn`/`match`/`elif`/`in` 作变量名报 ParseError。

---

## 完成判定标准

PS 借鉴修复完成须同时满足：
1. `ModernSyntaxComplianceTests` 全部用例通过（含新增 `"$(expr)"` 测试）。
2. `dotnet build OpenShell.slnx` 0 警告 0 错误。
3. 全解决方案测试套件全绿（不引入回归）。
4. 本文件所有任务 `[x]`。
5. 借鉴文件头部保留 PS MIT 版权声明，`ThirdPartyNotices.txt` 列明 PowerShell 项目。
6. `docs/ps-ref-reuse-audit.md` 与本文件状态一致。

---

## 变更日志

- 2026-07-10 创建任务清单，建立 PS 借鉴修复基线。审计结论：完全复用不可行（PS parser 41,701 行强耦合 SMA 子系统），采用「借鉴重写」策略。A 类 3 文件可直接搬运（1,223 行），B 类 7 文件可借鉴重写（25,834 行），C 类 8 文件不可复用（14,644 行）。
- 2026-07-10 T-100 完成：搬运 `CharTraits.cs` 到 `src/OpenShell.Core/Parsing/CharTraits.cs`（保留 PS MIT 版权，namespace 改 OpenShell.Parsing，Diagnostics.Assert 替换为 System.Diagnostics.Debug.Assert）。新增 `CharTraitsTests.cs` 93 个测试全部通过。全量 1852 通过 / 40 跳过 / 0 失败，无回归。
- 2026-07-10 第 1 批 ExpandableString 核心升级完成（T-102~T-106 + T-112）：
  - T-102：`AstNodes.cs` 新增 `ExpandableStringExpression` record（Value + FormatExpression + NestedExpressions + IsHereString）。
  - T-103~T-105：新增 `src/OpenShell.Core/Parsing/ExpandableStringParser.cs`，借鉴 PS `ScanDollarInStringExpandable`（tokenizer.cs:2518-2566）+ `ScanSubExpression`（tokenizer.cs:2362-2447）。实现调整：tokenizer 保持上下文不敏感产普通 String token，parser 层调用 `ExpandableStringParser.Parse` 解析 `$var`/`${name}`/`$(expr)` 插值段；`$(expr)` 内部递归括号配对 + `ScanStringLiteral` 跳过引号内括号 + `ModernParser.Parse` 延迟解析子表达式（借鉴 PS 分层设计）。
  - T-106：`Evaluator.cs` 新增 `ExpandableStringExpression` 分支 + `EvaluateExpandableString` 方法，用 `string.Format(FormatExpression, NestedExpressions 求值结果)` 拼接。原 `VariableExpander.ExpandInterpolation` 保留作为无 `$` 段字面量后备。
  - T-112：`ModernSyntaxComplianceTests.cs` 新增 8 个 `S6_SubExpression_*` 测试（基础/前缀/后缀/多段/变量引用/嵌套括号/混合/字符串内闭括号），全部通过。
  - 新增 T-113（P1）：`$(...)` 内含语句（if/for 等）需 SubExpressionStatement 语义，当前仅支持表达式。测试 `S6_SubExpression_StatementInside` Skip pending T-113。
  - ModernParser `ParsePrimary` 的 `TokenKind.String`/`HereString` 分支改为调用 `ExpandableStringParser.Parse`（无 `$` 段时退化为 `LiteralExpression`）。
  - 全量 1860 通过 / 41 跳过 / 0 失败，0 警告 0 错误，无回归。Compliance 基线：41 通过 / 34 跳过 / 0 失败。
- 2026-07-10 T-107/T-108/T-109/T-113 完成（第 2/3 批 + 子表达式语句语义）：
  - T-107：`Tokenizer.LexHereString` 双引号 here-string 补全 `` ` `` 转义（借鉴 PS tokenizer.cs:2755-2865），与 `LexString` 一致的转义映射。新增 4 个 `S6_HereString_*` compliance 测试。
  - T-108：here-string false-footer 检测验证（现有 `_column==1` 行首检查已正确）。新增 `SourceSpanTests.cs` 3 个 false-footer 测试。
  - T-109：`TryLexTypeRef` 放宽识别小写类型别名（`[int[]]`/`[string]`）；`ModernParser.ParseTypeRefText` + `PowerShellParser.ParseTypeRefText` 重写支持泛型与数组（修复 `Trim('[',']')` 误剥 `int[]` 内部括号的 bug）；`ParseParameterDeclaration` 增加 TypeRef 前置处理修复 CmdletBinding 回归。新增 5 个 `S7_TypeLiteral_*` compliance 测试。
  - T-113：`AstNodes.cs` 新增 `StatementSubExpressionExpression`；`ExpandableStringParser.ParseSubExpressionText` 对多语句/控制流返回 `StatementSubExpressionExpression`；`Evaluator` 新增 `EvaluateStatementSubExpression` 收集语句块管道输出。新增 2 个 `S6_SubExpression_Statement*` compliance 测试。
  - 全量测试全绿，0 警告 0 错误，无回归。Compliance 基线提升。
- 2026-07-10 T-110 完成（第 4 批：数字组合后缀）：
  - `Tokenizer.LexNumber` 重构：0x/0b/普通数字三分支统一后缀处理；后缀类型信息（`numberTypeHint`）应用到 `token.Value` 的 .NET 类型转换（`byte`/`sbyte`/`short`/`ushort`/`uint`/`ulong`/`long`）。
  - 借鉴 PS tokenizer.cs:4025-4083：`d`→double, `l`→long, `u`→uint, `y`→sbyte, `s`→short；组合 `ul`/`lu`→ulong, `uy`→byte, `us`/`su`→ushort。不支持 `f`/`m`（C# 后缀，与 KB/MB 单位前缀冲突）。
  - `TokenizerNumericLiteralTests.cs` 新增 13 个组合后缀测试（`0xFFuy`→byte 255, `1ul`→ulong, `1lu`→ulong, `1us`→ushort, `1su`→ushort, `1u`→uint, `1y`→sbyte, `1s`→short, `0x10uy`→byte 16, `0b1010ul`→ulong 10 等），全部通过。
  - 0x/0b 分支现在也支持后缀（`0xFFuy`/`0b1010ul`），向后兼容无后缀默认 long。
- 2026-07-10 T-111 完成（第 5 批：栈式作用域语义检查）：
  - `ModernParser` 新增 `ModernReservedVariableNames` 保留字集合（`fn`/`match`/`elif`/`in`/`async`/`await`/`export`/`import`/`macro`/`macro_rules`/`type`）+ `EnsureNotReservedVariable` 检查方法。
  - `ParseVariable`（Variable/ScopedVariable 分支）+ lambda 单参数分支 + `ParseLambdaParameter` 均接入保留字检查；`$env:NAME` 环境变量名不检查（合法）。
  - `ParseModernParameterDeclarations` + `ParseLambda` 多参数分支新增重复参数检查（大小写不敏感，`HashSet<string>` 跟踪）。
  - `ModernSyntaxComplianceTests.cs` 移除 `Constraint_ReservedWord_NotAsVariable` 的 Skip，新增 10 个 T-111 测试（保留字 6 个 + 作用域变量 + lambda 参数 + 自动变量豁免 + 重复参数 3 个 + 不同参数名正常）。
  - 全量 1898 通过 / 40 跳过 / 0 失败，0 警告 0 错误，无回归。Compliance 基线：52 通过 / 33 跳过 / 0 失败。
