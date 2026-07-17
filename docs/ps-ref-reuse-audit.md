# PowerShell 参考源码可复用性审计报告

- **审计日期**: 2026-07-10
- **审计对象**: `C:\Users\blmpt\Downloads\powershell-ref`（PowerShell 官方仓库，MIT 许可证）
- **审计范围**: `src/System.Management.Automation/engine/parser/` 目录（18 文件，41,701 行）
- **审计目的**: 判断是否可复用 PowerShell parser 源码到 OpenShell，以及在 OpenShell 上实现现代语法的最佳路径
- **审计结论**: **完全复用不可行**；采用「借鉴重写」策略，重点补强 OpenShell 现有 parser 的薄弱点

> 本文件为**外置待做文档**的一部分。修复进度由 `docs/ps-ref-reuse-tasks.md` 追踪，验证由 `ModernSyntaxComplianceTests` 测试套件保障。详见 `agents.md`「任务追踪规范」。

---

## 一、总体结论

PowerShell（PS）parser 与 OpenShell 在架构上有**三处根本性差异**，决定了复用策略以「借鉴重写」为主而非「直接搬运」：

### 1.1 架构差异

| 维度 | PowerShell | OpenShell |
|------|-----------|-----------|
| Tokenizer 模式 | **模式驱动**（`TokenizerMode` 枚举：Command/Expression/TypeName/Signature，`tokenizer.cs:490-495`） | **上下文不敏感**（`Tokenizer.cs:6` 注释），parser 决策命令模式 vs 表达式模式 |
| 双引号字符串 | tokenizer 产出 `StringExpandableToken`（含 `FormatString` + `NestedTokens`），parser 构造 `ExpandableStringExpressionAst`（`ast.cs:9825-9974`） | tokenizer 原样存文本，evaluator 运行时插值（`Token.cs:23` 注释） |
| AST 设计 | `class Ast` + 可变 + `AstVisitor`/`ICustomAstVisitor` 双 visitor 模式 | `record AstNode`（不可变）+ 模式匹配 |
| 求值方式 | `Compiler.cs` 编译为 `System.Linq.Expressions` 表达式树 | tree-walking interpreter（`Evaluator.cs`） |
| 节点规模 | ~60 类（`ast.cs` 9,373 行） | ~50 record（`AstNodes.cs` 513 行） |
| 总规模 | 41,701 行（仅 parser 目录） | 6,521 行（Parsing 目录） |

### 1.2 关键判断

1. **PS tokenizer 单模式 vs OpenShell 双模式**：PS tokenizer 的 `TokenizerMode` 状态机是核心，命令模式 vs 表达式模式的词法差异（如 `Generic` token 允许 `-`/`\`/`:`、数字不消费 `-` 前缀）全部在 tokenizer 内消化。OpenShell 设计为「上下文不敏感 tokenizer + parser 决策」。直接搬运 PS tokenizer 会破坏 OpenShell 架构，且 PS 模式不含 modern 语法的 `==`/`&&`/`=>` 等。

2. **PS AST 与 OpenShell AST 差异极大，替换不可行**：OpenShell AST 已含 modern 语法（async/await、match、宏、ESM、自定义类型）等 PS 完全没有的节点，替换会丢失这些能力。正确策略是**补充 OpenShell 缺失节点**（如 `ExpandableStringExpression`），而非替换。

3. **PS parser 含 Compiler，剥离后能独立工作**：`Parser.cs`/`tokenizer.cs`/`ast.cs` 与 `Compiler.cs` 是生产-消费关系，Compiler 不反向影响 parser。`Parser.cs` 全文无 `using System.Linq.Expressions`。剥离 Compiler 后 parser 正常工作。

4. **PS tokenizer `$(...)` 子表达式采用分层延迟解析**：tokenizer 产出 `UnscannedSubExprToken`（携带原始字符串），parser 再二次解析为 `ExpressionAst`。这是值得借鉴的分层设计。

---

## 二、文件级可复用性分类

### 2.1 A 类：可直接搬运（3 文件，1,223 行）

| 文件 | 行数 | 理由 |
|------|------|------|
| `CharTraits.cs` | 368 | 纯字符分类表（`CharTraits` flags + 128 项查表 + `CharExtensions`）。零外部依赖，仅依赖 `Diagnostics.Assert`。OpenShell 当前散用 `char.IsDigit/Letter` 等，可统一换用此表获得一致性（含特殊 dash、智能引号识别）。 |
| `Position.cs` | 662 | `IScriptExtent` / `InternalScriptExtent` / `Position` 偏移↔行列转换。逻辑独立，OpenShell 的 `SourceSpan.cs`（15 行）可对照增强（当前只有 line/column，缺 offset 体系）。 |
| `PreOrderVisitor.cs` | 193 | 纯前序遍历 visitor 基类，无外部依赖。OpenShell 用 record + 模式匹配，价值有限但可备用。 |

### 2.2 B 类：可借鉴重写（7 文件，25,834 行）

| 文件 | 行数 | 借鉴价值 |
|------|------|----------|
| `token.cs` | 1,289 | `TokenKind` 枚举（约 200 项）+ `TokenFlags` + `Token` 基类 + `StringExpandableToken`/`StringToken`/`NumberToken`/`ParameterToken` 子类层次。**关键借鉴点**：`StringExpandableToken` 携带 `FormatString` + `NestedTokens` 的设计——这是 OpenShell 实现 ExpandableString 的 token 层模板。 |
| `tokenizer.cs` | 4,403 | 核心词法算法（here-string 状态机、`$(...)` 子表达式、数字后缀、类型名扫描）极具价值，但强依赖 `TokenizerMode` 状态机与 `TokenList`/`Mode` 切换（`tokenizer.cs:2518-2549` 的 `ScanDollarInStringExpandable` 临时切换 `Mode = TokenizerMode.Expression`）。OpenShell 无模式概念，需重构为「parser 回调 tokenizer」或「tokenizer 产出带标记的原始段」。 |
| `ast.cs` | 9,373 | AST 节点定义参考。OpenShell 已有自研 record AST（`AstNodes.cs` 513 行），节点更丰富（含 modern 语法、async、宏、ESM）。**关键借鉴点**：`ExpandableStringExpressionAst`（`ast.cs:9825-9974`）与 `StringConstantExpressionAst`（`ast.cs:9732-9819`）的区分设计。 |
| `Parser.cs` | 7,205 | 递归下降文法规则（`ScriptBlockRule`/`ParamBlockRule`/`CommandRule`/`ArrayLiteralRule`/`HashExpressionRule`）文法清晰，注释含 BNF。但强依赖 tokenizer 的 `SetTokenizerMode` 与 `Resync`/`GetRestorePoint` 回溯机制。OpenShell parser 用 Pratt + 手写递归下降，回溯策略不同。 |
| `SemanticChecks.cs` | 2,127 | 语义检查模式（作用域栈 `_scopeStack`/`_memberScopeStack`、重复参数检查、保留字检查 `KeywordParameterReservedForFutureUse`）。OpenShell 当前无独立语义检查阶段，可借鉴其 visitor 遍历 + 栈式作用域设计。 |
| `VariableAnalysis.cs` | 1,535 | 变量数据流分析（def-use、所有路径返回分析 `AnalyzeMemberFunction`）。逻辑独立于运行时，OpenShell 若做静态诊断可借鉴。 |
| `AstVisitor.cs` | 539 | `AstVisitor` 抽象基类 + `AstVisitAction` 枚举。OpenShell 用 record 模式匹配，但若需可插拔遍历可借鉴此模式。 |

### 2.3 C 类：不可复用（8 文件，14,644 行）

| 文件 | 行数 | 不可复用原因 |
|------|------|--------------|
| `Compiler.cs` | 6,103 | 编译为 `System.Linq.Expressions`，依赖 `CachedReflectionInfo`（数百个反射句柄）/`Runspaces`/`ArrayOps`/`Instruction`。OpenShell 是 tree-walking，剥离后 parser 仍可工作。 |
| `TypeInferenceVisitor.cs` | 2,916 | 依赖 `PSObject`/`TypeResolver`/运行时类型推断，OpenShell 是动态类型 tree-walking。 |
| `DebugViewWriter.cs` | 1,042 | 仅供调试器 AST 可视化，依赖 `Expression` 树。 |
| `PSType.cs` | 1,343 | `TypeDefiner`，用 `Reflection.Emit` 动态生成类，依赖 `SessionStateKeeper`/`CustomAttributeBuilder`。OpenShell 自定义类型（`TypeDefinitionStatement`）走元数据注册。 |
| `TypeResolver.cs` | 873 | 依赖 `LanguagePrimitives.ConvertTo`/`TypeAccelerators`/`SessionState` 类型表，深度耦合 PS 类型系统。 |
| `SafeValues.cs` | 629 | `SafeValueVisitor` 依赖 `RestrictedLanguageChecker`/安全沙箱，耦合 SMA 安全子系统。 |
| `SymbolResolver.cs` | 676 | 依赖 `SessionState`/`PSVariable`/命令查找，解析符号需运行时上下文。 |
| `ConstantValues.cs` | 425 | `ConstantValueVisitor` 深度依赖 `PSObject`/`LanguagePrimitives`/`Conversion` 子系统，求值常量参数。OpenShell evaluator 自研。 |

---

## 三、关键技术点实现定位

### 3.1 here-string `@"..."@` 词法状态机

PS 实现位于 `tokenizer.cs:2578-2865`，由三个方法协作：

- **`ScanAfterHereStringHeader`**（`tokenizer.cs:2578-2635`）：校验 `@"` 后必须跟换行，否则报 `UnexpectedCharactersAfterHereStringHeader`。
- **`ScanPossibleHereStringFooter`**（`tokenizer.cs:2637-2683`）：检测行首 `'@`/`"@`，处理「终止符前有空白」的误导情形，记录 `falseFooterOffset` 用于精准报错。
- **`ScanHereStringExpandable`**（`tokenizer.cs:2755-2865`，双引号）：第 2804-2810 行调用 `ScanDollarInStringExpandable` 处理 `$var`/`$(...)`，第 2811-2826 行处理 `` ` `` 转义。

**OpenShell 现状**（`Tokenizer.cs:274-302` `LexHereString`）：实现极简，仅检查 `_column == 1 && _source[_pos] == quote && Peek(1) == '@'`。**缺口**：无 false-footer 检测、无双引号 here-string 的 `$` 插值词法、无 `` ` `` 转义处理、无错误恢复。

### 3.2 双引号字符串变量插值 `"$var"` / `"$(expr)"` / `"${name}"`

PS 核心是 **`ScanDollarInStringExpandable`**（`tokenizer.cs:2518-2566`）：

- **`$var`**：第 2538-2542 行，复用 `ScanVariable`。
- **`$(expr)`**：第 2533-2537 行，调用 **`ScanSubExpression`**（`tokenizer.cs:2362-2447`），该法递归扫描括号配对，产出 `UnscannedSubExprToken`（第 2446 行），**子表达式内容不被立即解析**，留给 parser 二次解析。
- **`${name}`**：由 `ScanVariable` 内部处理（`c1 == '{'` 分支）。
- **格式串生成**：第 2554 行 `formatSb.Append('{'); formatSb.Append(nestedTokens.Count); formatSb.Append('}')` 生成 `{N}` 占位符。

**关键设计**：PS tokenizer 产出可插值段 token 列表（`nestedTokens`）+ `FormatString`（`{0}{1}` 形式），parser 再把 `nestedTokens` 解析为 `ExpressionAst` 填入 `ExpandableStringExpressionAst.NestedExpressions`（见 `Parser.cs:6367-6369` 的 `ParseNestedExpressions`）。最终求值用 `string.Format`。

**OpenShell 现状**（T-088 已完成简化修复）：`VariableExpander.ExpandInterpolation`（`VariableExpander.cs:153-212`）在 evaluator 运行时插值，支持 `$var`/`${name}`/`$?`/`$env:NAME`/`$var.Prop`/`$arr[i]`，但 `"$(expr)"` 子表达式抛 `NotSupportedException`。**升级方向**：借鉴 PS 的分层设计，tokenizer 产出 `StringExpandableToken`，parser 构造 `ExpandableStringExpressionAst`，evaluator 用 `string.Format`。

### 3.3 number literal（含 0x/0b/KB/MB/GB 后缀）

PS 实现位于 `tokenizer.cs:3884-3918`（`ScanNumber`）+ `tokenizer.cs:3933-4060+`（`ScanNumberHelper`）：

- 0x/0b：`tokenizer.cs:3960-3989`，`ScanHexDigits`/`ScanBinaryDigits`。
- 类型后缀 `u/s/l/d/y/n`：`tokenizer.cs:4025-4058`，支持组合（如 `uy`=unsigned byte，4062-4083）。
- 数量单位 KB/MB/GB/TB/PB：在 `ScanNumberHelper` 后段。
- 范围运算符消歧：`tokenizer.cs:3998-4005`，遇 `..` 时 `UngetChar` 第一个 `.`。

**OpenShell 现状**（`Tokenizer.cs:487-588` `LexNumber`）：已支持 0x/0b/`.`/`e`/后缀 `d/l/u/y/s`/KB/MB/GB/TB/PB。**基本对齐**，但缺组合后缀（`uy`/`ul`/`sl`）与 `n`（BigInteger）。可对照 PS 的 `NumberSuffixFlags` flags 组合逻辑增强。

### 3.4 type literal `[System.IO.File]` 词法

PS 实现依赖 **`TokenizerMode.TypeName`**：

- `Parser.cs:1308-1329`：parser 解析 `[` 时 `SetTokenizerMode(TokenizerMode.TypeName)`。
- `tokenizer.cs:4423-4428`：`InTypeNameMode()` 时调用 **`ScanTypeName`**（`tokenizer.cs:4462-4493`），该法接受 `.`/`` ` ``/`_`/`+`/`#`/`\\` 与字母数字。
- 数组后缀 `int[]`：由 parser 在 `TypeNameRule` 处理（解析 `[]` 后缀）。

**OpenShell 现状**（`Tokenizer.cs:696-738` `TryLexTypeRef`）：用「试探 + 回退」——扫描到匹配 `]`，判断是否类型。**问题**：不支持 `[int[]]`（含 `[]`）、不支持泛型 `List[int]`（被 `Contains(',')` 误判为特性）。可借鉴 PS 的 TypeName 模式 + parser 侧解析。

### 3.5 command argument 模式 vs expression 模式切换

PS 核心机制：**`TokenizerMode`** + parser 主动切换。

- **`CommandRule`**（`Parser.cs:6444-6563+`）：进入时 `SetTokenizerMode(TokenizerMode.Command)`（6482）。命令模式下 tokenizer 产出 `Generic` token（允许含 `-`/`/`/`\` 等）。
- **`GetCommandArgument`**（`Parser.cs:6295-6395+`）：第 6355-6382 行处理 `Generic` token，若是 `StringExpandableToken` 则调用 `ParseNestedExpressions` 构造 `ExpandableStringExpressionAst`。
- **模式切换点**：`GetSingleCommandArgument`（`Parser.cs:6266-6283`）、`HashExpressionRule`（`Parser.cs:7413-7428`）等处。

**OpenShell 现状**：tokenizer 无模式，`Identifier` lex 时已尝试合并 verb-noun（`Tokenizer.cs:852-874`），但 `-eq` 等运算符与命令名消歧靠 `TryMapPsOperator` 试探回退。**这是双模式改造的核心难点**——PS 靠 tokenizer 模式隔离命令模式词法，OpenShell 需在 parser 层用 lookahead 或 token 重扫实现等价语义。

### 3.6 scriptblock `{ ... }`、param block、begin/process/end 块

PS 实现：

- **`ScriptBlockRule`**（`Parser.cs:772-809`）：顺序 `using-statements` → `param-block` → `statement-terminators` → `script-block-body`。关键在第 796-804 行：先试探 `ParamBlockRule`，失败则 `Resync(restorePoint)` 回溯。
- **`ParamBlockRule`**（`Parser.cs:845-911`）：`[attrs] param ( param-list )`。
- **begin/process/end**：由 `ScriptBlockBodyRule` 处理命名块。`SemanticChecks.cs:404-413` 校验方法体内不允许命名块。

**OpenShell 现状**：`ScriptBlockExpression`（`AstNodes.cs:350-359`）已含 `BeginBlock`/`ProcessBlock`/`EndBlock` 字段，结构对齐。PS 的 `Resync` 回溯机制值得借鉴（OpenShell parser 若需试探性解析 param block 可参考）。

### 3.7 array `@()`、hash `@{}` 字面量

PS 实现：

- **数组**：逗号字面量 `1,2,3` 走 **`ArrayLiteralRule`**（`Parser.cs:6975-7021`）：`UnaryExpressionRule` + 循环吃逗号，产出 `ArrayLiteralAst`。
- **哈希**：**`HashExpressionRule`**（`Parser.cs:7341-7403`）：`@{` 后循环 `GetKeyValuePair`（7405-...），键解析时临时切 `TokenizerMode.Expression`（7413-7428），产出 `HashtableAst`（7400）。

**OpenShell 现状**：`ArrayExpression`（`AstNodes.cs:364`）/`HashExpression`（366）节点已存在。parser 实现需对照 PS 的逗号消歧与键表达式模式切换。

### 3.8 ExpandableStringExpressionAst（OpenShell 缺失）

PS 定义于 `ast.cs:9825-9974`：

- **字段**：`Value`（原始未展开文本）、`FormatExpression`（`{0}{1}` 形式，由 tokenizer 生成）、`NestedExpressions`（`ReadOnlyCollection<ExpressionAst>`，恒为 `VariableExpressionAst` 或 `SubExpressionAst`，见 9922-9923 注释）、`StringConstantType`（DoubleQuoted/DoubleQuotedHereString/BareWord）。
- **构造**：`ast.cs:9857` 调用 `Language.Parser.ScanString(value)` 二次扫描拆分嵌套表达式。
- **求值**：`FormatExpression` + `NestedExpressions` 求值后 `string.Format`。

**OpenShell 缺口**：`AstNodes.cs` 无此节点。双引号字符串统一进 `LiteralExpression(Kind=String)`。**建议新增** `ExpandableStringExpression(string Value, string FormatExpression, IReadOnlyList<Expression> NestedExpressions, SourceSpan Span)`，并在 tokenizer/parser 侧补全嵌套表达式解析。

### 3.9 StringConstantExpressionAst vs ExpandableStringExpressionAst 区分

PS 区分逻辑见 `ast.cs:9786-9803` `MapTokenKindToStringConstantKind`：

| TokenKind | AST 节点 |
|-----------|----------|
| `StringLiteral`（单引号） | `StringConstantExpressionAst`(SingleQuoted) |
| `StringExpandable`（双引号，**无**嵌套） | `StringConstantExpressionAst`(DoubleQuoted) |
| `StringExpandable`（双引号，**有**嵌套） | `ExpandableStringExpressionAst` |
| `HereStringLiteral`（`@'`） | `StringConstantExpressionAst`(SingleQuotedHereString) |
| `HereStringExpandable`（`@"`，无嵌套） | `StringConstantExpressionAst`(DoubleQuotedHereString) |
| `HereStringExpandable`（`@"`，有嵌套） | `ExpandableStringExpressionAst` |

**关键**：PS 在 `Parser.cs:6362-6373` 判断——若 `expandableToken` 的 `nestedExpressions` 非空才造 `ExpandableStringExpressionAst`，否则降级为 `StringConstantExpressionAst`。OpenShell 可采用同策略。

### 3.10 SemanticChecks 保留字检查、变量作用域分析

- **保留字**：`SemanticChecks.cs:461-468`、`498-503`、`508-513` 报 `KeywordParameterReservedForFutureUse`。PS 无独立「保留字表」检查，而是按语句类型散布校验。
- **作用域分析**：`SemanticChecks.cs:36-37` 双栈 `_memberScopeStack`（类成员）+ `_scopeStack`（脚本块）。`VisitScriptBlock`（1227 行 push）/`VisitFunctionMember`（394 行 push）。变量数据流委托 `VariableAnalysis.AnalyzeMemberFunction`（423 行）。

**OpenShell 现状**：T-111 已实现保留字 + 重复参数检查（`ModernParser`）：`ModernReservedVariableNames` 集合禁止 `fn`/`match`/`elif`/`in`/`async`/`await`/`export`/`import`/`macro`/`type` 作变量名；`ParseModernParameterDeclarations` 与 `ParseLambda` 多参数分支检查大小写不敏感重复参数。未实现 PS 的完整栈式作用域变量数据流分析（未来扩展点）。

---

## 四、与 OpenShell 现有修复任务的关系

本审计产出的任务清单（`docs/ps-ref-reuse-tasks.md`）与现有 `docs/modern-syntax-tasks.md` 的关系：

| 现有任务 | 升级关系 | 说明 |
|----------|----------|------|
| T-088（已完成简化修复） | 被 T-103~T-106 升级 | T-088 的运行时插值（`VariableExpander.ExpandInterpolation`）保留作为基础，T-103~T-106 借鉴 PS 分层设计实现 `"$(expr)"` 子表达式 |
| T-083（`${expr}` 任意表达式） | 被 T-104 覆盖 | T-104 实现 `ScanSubExpression` 后，`${expr}` 与 `$(expr)` 统一处理 |
| T-089（三引号缩进 bug） | 独立 | 不受 PS 借鉴影响 |
| T-001（Tokenizer 模式感知） | 调整方向 | 原计划引入 modern/ps1 LexMode；PS 借鉴后改为「tokenizer 产出带标记 token + parser 决策」，不引入全局 Mode |
| T-002（PowerShellParser 拒绝现代运算符） | 独立 | 已部分完成（移除 `==`/`!=`/`<`/`>`/`<=`/`>=`/`&&`/`||`），不依赖 PS 借鉴 |
| T-003（break label） | 独立 | 不受 PS 借鉴影响 |

**原则**：modern-syntax-tasks.md 保留为权威任务清单，ps-ref-reuse-tasks.md 是其**实施路径升级**——把原本的"自研补丁"任务升级为"借鉴 PS 的分层设计"任务，任务 ID 独立（T-100+），不与 modern-syntax-tasks.md 冲突。

### 4.1 修复中发现的新限制（T-113）— 已解决

第 1 批 ExpandableString 核心升级（T-102~T-106）实现过程中发现：

- **`$(...)` 内仅支持表达式，不支持语句**：原 `ExpandableStringParser.ParseSubExpressionText` 仅处理 `ExpressionStatement` 与单命令 `PipelineStatement`。PS 中 `$(...)` 可含任意语句（`if`/`for`/`foreach`/`switch` 等）并返回末语句的管道输出。
- **修复状态**：**T-113 已完成**。新增 `StatementSubExpressionExpression` AST 节点承载语句块；`ParseSubExpressionText` 对多语句/控制流返回该节点；`Evaluator.EvaluateStatementSubExpression` 执行语句块并收集管道输出作为表达式值。测试 `S6_SubExpression_StatementInside` / `S6_SubExpression_MultipleStatements` 已移除 Skip 并通过。
- **残留限制**：`$(if $true { ... })` 中 `if` 仍需括号包裹条件（`if ($true)`），无括号形式 `if $true` 依赖 T-040（现代语法 if 无括号），属独立任务。

---

## 五、推荐实施路径

按依赖关系与优先级分批：

### 第 0 批：基础设施搬运（A 类文件）
- T-100 搬运 `CharTraits.cs`（368 行，零依赖）
- T-101 搬运 `Position.cs` offset 体系（增强 `SourceSpan.cs`）

### 第 1 批：ExpandableString 分层实现（核心升级）
- T-102 新增 `ExpandableStringExpression` AST 节点
- T-103 tokenizer 双引号字符串产出 `StringExpandableToken`（携带 `FormatString` + `NestedTokens`）
- T-104 tokenizer 实现 `ScanSubExpression` 处理 `$(...)`
- T-105 parser 解析 `StringExpandableToken` 的 `NestedTokens` 为 AST
- T-106 evaluator 改用 `ExpandableStringExpression` 求值（`string.Format` 模式）

### 第 2 批：here-string 增强
- T-107 here-string 双引号版补全 `$` 插值与 `` ` `` 转义（借鉴 `tokenizer.cs:2755-2865`）
- T-108 here-string false-footer 检测与错误恢复

### 第 3 批：类型字面量增强
- T-109 类型字面量支持 `[int[]]`/`List[int]`（借鉴 `ScanTypeName` + parser 侧 TypeName 模式）

### 第 4 批：数字字面量增强
- T-110 数字字面量补全组合后缀（`uy`/`ul`/`sl`/`n`）

### 第 5 批：语义检查阶段（可选，低优先）
- T-111 引入栈式作用域语义检查（借鉴 `SemanticChecks.cs`）

---

## 六、许可证说明

PowerShell 源码采用 **MIT 许可证**（`C:\Users\blmpt\Downloads\powershell-ref\LICENSE.txt`）。MIT 许可证允许复用、修改、分发，仅需保留版权声明。OpenShell 借鉴 PS 代码须：
1. 在借鉴的文件头部保留 `// Copyright (c) Microsoft Corporation. Licensed under the MIT License.` 声明
2. 在 `ThirdPartyNotices.txt` 或等效文件中列明 PowerShell 项目

---

## 七、相关文件路径

### PowerShell 参考源码
- `C:\Users\blmpt\Downloads\powershell-ref\src\System.Management.Automation\engine\parser\tokenizer.cs`
- `C:\Users\blmpt\Downloads\powershell-ref\src\System.Management.Automation\engine\parser\Parser.cs`
- `C:\Users\blmpt\Downloads\powershell-ref\src\System.Management.Automation\engine\parser\ast.cs`
- `C:\Users\blmpt\Downloads\powershell-ref\src\System.Management.Automation\engine\parser\CharTraits.cs`
- `C:\Users\blmpt\Downloads\powershell-ref\src\System.Management.Automation\engine\parser\Position.cs`
- `C:\Users\blmpt\Downloads\powershell-ref\src\System.Management.Automation\engine\parser\SemanticChecks.cs`

### OpenShell 当前实现
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Parsing\Tokenizer.cs`
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Parsing\Token.cs`
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Parsing\Ast\AstNodes.cs`
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Parsing\ModernParser.cs`
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Parsing\PowerShellParser.cs`
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Variables\VariableExpander.cs`
- `c:\Users\blmpt\Downloads\workspace\openshell\src\OpenShell.Core\Runtime\Evaluator.cs`
