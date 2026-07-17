# 脚本实例端到端测试体系任务清单

- **创建日期**: 2026-07-11
- **基准文档**: `docs/script-e2e-audit.md`（审计）、ADR-0050 §10、ADR-0056、ADR-0054、ADR-0033 §5
- **验证机制**: `tests/OpenShell.Core.Tests/ScriptE2E/ScriptE2EComplianceTests.cs`（合规测试套件）
- **追踪规范**: 见 `agents.md`「任务追踪规范」。本文件为脚本 E2E 测试体系的唯一权威任务清单。

---

## 状态图例

- `[ ]` 未开始
- `[~]` 进行中
- `[x]` 已完成（测试通过）
- `[!]` 阻塞（注明原因）

## 优先级图例

- **P0** 阻断性缺陷（功能不可用）—— 必须最先修
- **P1** 测试体系建立（覆盖已实现功能）
- **P2** 增强/收尾（跨语法互操作、综合场景）

---

## 任务总表

| ID | 优先级 | ADR | 描述 | 状态 | 依赖 | 测试 |
|----|--------|-----|------|------|------|------|
| T-200 | P0 | §0056 | ModuleRegistry DI 接线：创建 `AddScriptModules()` 扩展方法 + 注册到 Program.cs/AppBuilder.cs/TestHostBuilder | `[x]` | — | Compliance §模块 |
| T-201 | P0 | §0054 | TestHostBuilder 注册 IExecutionPolicyService（`AddExecutionPolicy()`），测试用 Bypass 策略 | `[x]` | — | Compliance §策略 |
| T-202 | P0 | §0050 §10 | Tokenizer IsKeyword 注册 `import` 关键字（D-202） | `[x]` | — | Compliance §import |
| T-203 | P0 | §0056 §2 | Tokenizer IsKeyword 注册 `from`/`as` 关键字（D-203） | `[x]` | T-202 | Compliance §模块 |
| T-204 | P0 | §0050 §1.3 | 多行 `#lang ps1 { }` 块支持（D-204）：Tokenizer LexLineComment 检测到 `{` 时读至配对 `}` | `[x]` | — | Compliance §lang |
| T-205 | P0 | §0050 §5.3 | ParseTry 消费 catch 块后换行符，导致后续语句被吞（D-205） | `[x]` | — | Compliance §独立 |
| T-206 | P0 | §0056 §2 | 相对 import 路径解析：`import "../modules/x.osh"` 相对脚本文件目录而非 CWD（D-206） | `[x]` | — | Compliance §综合 |
| T-207 | P0 | §0050 §8.2 | ParsePostfixExpr LParen 分支丢弃 CommandExpression/VariableExpression 的参数（D-207） | `[x]` | — | Compliance §综合 |
| T-208 | P0 | §0050 §4.1 | EvaluateMember 把空参数列表 `[]` 误当属性访问，导致 `$s.ToUpper()` 返回 null（D-208） | `[x]` | — | Compliance §综合 |
| T-209 | P0 | §0050 §4.1 | InvokeMethod 不支持方法重载，`ToUpper()` 抛 AmbiguousMatchException（D-209） | `[x]` | — | Compliance §综合 |
| T-210 | P1 | — | 建立 `tests/TestData/Scripts/` 目录 + 脚本实例 fixture（modules/standalone/lang_blocks） | `[x]` | — | — |
| T-211 | P1 | §0050 §10.1 | `import "file.osh"` 副作用加载端到端测试 | `[x]` | T-200, T-210 | Compliance §import |
| T-212 | P1 | §0050 §10.1 | `import "file.ps1"` PS 脚本加载端到端测试 | `[x]` | T-200, T-210 | Compliance §import |
| T-213 | P1 | §0056 §2 | `import { fn } from "file.osh"` 命名导入端到端测试 | `[x]` | T-200, T-210 | Compliance §模块 |
| T-214 | P1 | §0056 §2 | `import * as Mod from "file.osh"` 命名空间导入端到端测试 | `[x]` | T-200, T-210 | Compliance §模块 |
| T-215 | P1 | §0056 §1 | `export fn/const/default` 导出声明端到端测试 | `[x]` | T-200, T-210 | Compliance §模块 |
| T-216 | P1 | §0056 §3 | ModuleRegistry 缓存去重测试（同文件多次 import 只加载一次） | `[x]` | T-200 | Compliance §模块 |
| T-217 | P1 | §0056 §3 | ModuleRegistry Remove/Clear 测试 | `[x]` | T-200 | Compliance §模块 |
| T-220 | P1 | §0050 §1.3 | `#lang ps1 { }` 块内函数定义+调用执行测试 | `[x]` | T-210 | Compliance §lang |
| T-221 | P1 | §0050 §1.3 | `#lang ps1 { }` 块内 PS 语法执行测试 | `[x]` | T-210 | Compliance §lang |
| T-230 | P1 | §0054 | ExecutionPolicy Restricted 禁止脚本执行测试 | `[x]` | T-201, T-210 | Compliance §策略 |
| T-231 | P1 | §0054 | ExecutionPolicy Bypass 无限制执行测试 | `[x]` | T-201, T-210 | Compliance §策略 |
| T-240 | P2 | §0050 §10 | 跨语法互操作：.osh 文件内 `#lang ps1 { }` 嵌入并调用块内函数 | `[x]` | T-220, T-211 | Compliance §interop |
| T-241 | P2 | §0050 §10 | 跨语法互操作：.osh 文件 import .ps1 文件 | `[x]` | T-212, T-213 | Compliance §interop |
| T-250 | P2 | — | 综合场景：多文件项目结构（主脚本 import 多个模块） | `[x]` | T-213, T-215 | Compliance §综合 |
| T-251 | P2 | — | 脚本文件错误处理（语法错误/运行时错误/文件不存在） | `[x]` | T-211 | Compliance §错误 |
| T-260 | P3 | — | ScriptE2EComplianceTests 合规测试套件建立 | `[x]` | — | — |

---

## 修复执行顺序

### 第 0 批：建立验证基线
- T-260 建立 `ScriptE2EComplianceTests`，已实现特性用 `[Fact]`，未实现/阻断特性用 `[Fact(Skip="pending T-XXX")]`。

### 第 1 批：P0 DI 接线修复（阻断性）
1. T-200 ModuleRegistry DI 接线
2. T-201 TestHostBuilder 注册 IExecutionPolicyService

**第 1 批完成后，模块系统和执行策略在所有 host 中可用。**

### 第 2 批：P1 测试体系建立
1. T-210 建立 TestData/Scripts/ 脚本实例 fixture
2. §import: T-211 / T-212
3. §模块: T-213 / T-214 / T-215 / T-216 / T-217
4. §lang 块: T-220 / T-221
5. §策略: T-230 / T-231

### 第 3 批：P2 互操作与综合场景
1. T-240 / T-241 跨语法互操作
2. T-250 / T-251 综合场景与错误处理

### 第 4 批：P3 收尾
移除所有 Skip，确保合规测试套件全绿。

---

## 完成判定标准

脚本实例 E2E 测试体系完成须同时满足：
1. `ScriptE2EComplianceTests` 全部用例通过（无 Skip）。
2. `dotnet build OpenShell.slnx` 0 警告 0 错误。
3. 全解决方案测试套件全绿（不引入回归）。
4. 本文件所有任务 `[x]`。
5. `tests/TestData/Scripts/` 下有覆盖各场景的 .ps1/.osh 脚本实例。

---

## 变更日志

- 2026-07-11 创建任务清单与审计文档（`docs/script-e2e-audit.md`）。审计发现 P0 缺陷 D-200（ModuleRegistry 未注册）+ P1 缺陷 D-201（TestHostBuilder 未注册 IExecutionPolicyService）。
- 2026-07-11 建立合规测试基线（T-260 `[x]`）+ TestData/Scripts/ 脚本实例 fixture（T-210 `[x]`）：
  - 新增 `tests/TestData/Scripts/` 目录：modules/（math.osh, strings.osh, legacy.ps1）、standalone/（hello.osh, control_flow.osh, ps1_script.ps1, main.osh）、lang_blocks/（mixed.osh）。
  - 新增 `tests/OpenShell.Core.Tests/ScriptE2E/ScriptE2EComplianceTests.cs`：20 个测试（4 通过 / 16 跳过 / 0 失败）。
  - `TestDataPaths.cs` 新增 `ScriptsRoot` / `ModulesDir` / `StandaloneDir` / `LangBlocksDir` 及各脚本路径属性。
  - 基线状态：4 通过 / 16 跳过 / 0 失败。全量 1956 通过 / 23 跳过 / 0 失败，0 警告 0 错误，无回归。
- 2026-07-11 审计中发现的**新缺陷**（回写审计文档）：
  - **D-202**（P0）：`import` 未在 Tokenizer `IsKeyword` 中注册 → `CheckKeyword("import")` 永远返回 false → `import "path"` 被解析为命令调用。新增 T-202。
  - **D-203**（P0）：`from` 未在 Tokenizer `IsKeyword` 中注册 → `import { fn } from "file"` 中 `MatchKeyword("from")` 失败。新增 T-203。
  - **D-204**（P0）：多行 `#lang ps1 { }` 块失败 → Tokenizer `LexLineComment` 只读首行 → `LangDirective` token 仅含 `#lang ps1 {` → `ExtractBraceContent` 找不到配对 `}` → `UnclosedLangBlockError`。新增 T-204。
  - **D-205**（P0）：ParseTry 在 catch 块后调用 `SkipNewLinesAndComments()` 消费换行符，导致 `ParseScript` 的错误恢复 `_pos++` 吞掉后续语句首 token。新增 T-205。
- 2026-07-11 **第 1 批 P0 修复完成**（T-200~T-209 全部 `[x]`）：
  - T-200：创建 `ModuleServiceCollectionExtensions.cs`（`AddScriptModules()`），注册到 Program.cs / AppBuilder.cs / TestHostBuilder。
  - T-201：TestHostBuilder `Build()` 调用 `AddExecutionPolicy()` + `AddScriptModules()`，测试上下文设置 Bypass 策略。
  - T-202/T-203：Tokenizer `IsKeyword` 注册 `import`/`from`/`as` 关键字。
  - T-204：Tokenizer `LexLineComment` 检测 `#lang` 多行块，新增 `ReadLangBlockIfMultiLine` 跨行读取至配对 `}`。
  - T-205：ModernParser `ParseTry` 保存/恢复位置，避免 `SkipNewLinesAndComments()` 消费分隔换行。
  - T-206：Evaluator 新增 `ResolveScriptPath()`，相对 import 路径相对 `CurrentModulePath` 目录解析（D-206）。
  - T-207：ModernParser `ParsePostfixExpr` LParen 分支增加 `CommandExpression`/`VariableExpression` 处理，不再丢弃参数（D-207）。
  - T-208：Evaluator `EvaluateMember` 区分 `Arguments is null`（属性访问）与 `Arguments is not null`（方法调用，即使空列表）（D-208）。
  - T-209：Evaluator `InvokeMethod` 按参数数量选择重载，解决 `AmbiguousMatchException`（D-209）。
- 2026-07-11 **第 2~4 批全部完成**（T-210~T-260 全部 `[x]`）：
  - 20 个 ScriptE2E 合规测试全部通过（0 Skip / 0 Fail）。
  - 全量测试 1972 通过 / 7 跳过 / 0 失败，0 警告 0 错误，无回归。
  - 修复中发现的**新缺陷**回写审计文档：D-206（相对路径解析）、D-207（LParen 参数丢弃）、D-208（空参数列表误判为属性访问）、D-209（方法重载歧义）。
