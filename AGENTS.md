# AGENTS.md — OpenShell Agent 协作规范

本文件定义 AI agent 在 OpenShell 仓库中工作时的协作约定。所有 agent 必须遵守。

---

## 任务追踪规范（强制）

**不再依赖内置待办（TodoWrite / 内联 todo 列表）进行任务追踪。一切任务追踪由外置待办清单与外置待做文档处理。**

### 外置文档体系

| 文档 | 用途 | 维护方式 |
|------|------|----------|
| `docs/<topic>-tasks.md` | 某主题的完整任务清单（含 ID、优先级、状态、依赖、对应测试） | 每个修复主题一个文件；任务用 `[ ]`/`[~]`/`[x]`/`[!]` 标记状态 |
| `docs/<topic>-audit.md` | 某主题的审计/调研报告（问题诊断、证据、严重度分级） | 修复开始前落地；修复中发现的 新缺陷回写此文档 |
| `tests/.../*ComplianceTests.cs` | 主题对应的合规测试套件（机械化验证） | 已实现特性 `[Fact]` 必须通过；未实现特性 `[Fact(Skip="pending T-XXX")]` |

### 工作流程

1. **修复前**：先在 `docs/` 落地审计文档与任务清单，建立合规测试套件基线（已实现特性通过、未实现特性 Skip）。
2. **修复中**：
   - 从任务清单选取任务，将状态改为 `[~]`（进行中）。
   - 实现后移除对应测试的 `Skip`，确保测试通过。
   - 将任务状态改为 `[x]`，在变更日志记录。
   - 修复中发现的**新缺陷**须新增任务 ID 并回写审计文档，不得遗漏。
3. **修复后**：确认 `dotnet build OpenShell.slnx` 0 警告 0 错误 + 全解决方案测试全绿 + 任务清单全部 `[x]`。

### 禁止事项

- **禁止**仅用内置 TodoWrite 追踪多步骤修复任务——必须落入外置任务清单。
- **禁止**在未建立合规测试基线前开始修改实现代码。
- **禁止**修改实现代码后不同步更新任务清单状态与审计文档。
- **禁止**将 ADR 头部 Implementation Status 标记为「已实现」而合规测试中仍有 Skip 项。

### 当前进行中的主题

- **现代语法（.osh）修复**：见 `docs/modern-syntax-audit.md`（审计）+ `docs/modern-syntax-tasks.md`（任务清单）+ `tests/OpenShell.Core.Tests/Parsing/ModernSyntaxComplianceTests.cs`（合规测试）。最新状态（2026-08-27 核实）：107 项合规测试全部通过 / 0 跳过 / 0 失败（原 33 个 `pending T-1xx` Skip 已随任务完成全部解除）。
- **PowerShell 参考源码借鉴**：见 `docs/ps-ref-reuse-audit.md`（审计）+ `docs/ps-ref-reuse-tasks.md`（任务清单）。审计结论：完全复用不可行（PS parser 41,701 行强耦合 SMA 子系统），采用「借鉴重写」策略。A 类 3 文件可直接搬运，B 类 7 文件可借鉴重写，C 类 8 文件不可复用。T-100~T-113 全部完成：CharTraits 搬运、SourceSpan offset 体系、ExpandableString 分层解析（`$(expr)` 子表达式插值）、here-string 转义/插值、类型字面量（`[int[]]`/`List[int]`）、数字组合后缀（`uy`/`ul`/`us`）、`$(...)` 语句语义、栈式作用域语义检查（保留字/重复参数）。许可证：PS 源码 MIT，借鉴文件须保留版权声明 + ThirdPartyNotices 列明。
- **脚本实例端到端测试体系**：见 `docs/script-e2e-audit.md`（审计）+ `docs/script-e2e-tasks.md`（任务清单）+ `tests/OpenShell.Core.Tests/ScriptE2E/ScriptE2EComplianceTests.cs`（合规测试）。T-200~T-260 全部完成：ModuleRegistry DI 接线（`AddScriptModules()`）、TestHostBuilder 注册 IExecutionPolicyService、Tokenizer 注册 `import`/`from`/`as` 关键字、多行 `#lang ps1 { }` 块支持、ParseTry 吞语句修复、相对 import 路径解析（D-206）、ParsePostfixExpr LParen 参数丢弃修复（D-207）、EvaluateMember 空参数列表误判修复（D-208）、InvokeMethod 方法重载支持（D-209）。合规测试全部通过 / 0 跳过 / 0 失败，全量 2119 通过 / 2 跳过（仅真实 SFTP 基础设施）/ 0 失败（2026-08-27 实测）。
- **GUI 与 CLI 产品化优化**：见 `docs/gui-cli-optimization-audit.md`（审计）+ `docs/gui-cli-optimization-tasks.md`（任务清单）+ `GuiCliOptimizationGuiComplianceTests.cs` / `GuiCliOptimizationCliComplianceTests.cs`（合规测试）。T-600~T-625 完成：语义主题、工作区列折叠、命令/标签栏、文件状态、会话标签恢复、可访问性、CLI 早期参数解析、干净流与退出码、SSH.NET 2026.0.0 安全升级（D-626）、非交互会话生命周期隔离（D-627）、AST 字符串数组绑定修复（D-628）。剩余：T-630 的 1200x800 / 800x500 双尺寸真实窗口截图复验（人工）。关联主题：project-stability 的 T-591 最终验收已于 2026-08-27 通过（见 `docs/project-stability-audit.md` §六）。
- **跨平台支持**：见 `docs/cross-platform-audit.md`（审计）+ `docs/cross-platform-tasks.md`（任务清单）。起因：仓库首次 PR 的 CI 暴露 ubuntu/macos 各 89 个失败（非 Windows 平台从未验证过）。T-700~T-704 完成：路径分隔符跨平台化（FS Provider/回收站/归档命令/GUI，D-700）、CliProcessRunner 跨平台解析（D-701）、Unix 进程 API 可靠性修复族（Wait-Process 轮询/Start-Process 竞态/测试唯一名隔离，D-702）、IPC 与日志契约平台适配（D-703~D-705）。Windows + WSL 本地全量均 2120 通过 / 2 跳过 / 0 失败。注意：`artifacts/bin/OpenShell/{cfg}` 为 Cli/Gui host 固定共享输出目录，跨平台交替构建后需强制重建。
- **可靠性与安全加固（improvement-hardening）**：见 `docs/improvement-hardening-audit.md`（审计）+ `docs/improvement-hardening-tasks.md`（任务清单）+ `tests/OpenShell.Core.Tests/ImprovementHardeningComplianceTests.cs`（合规测试）。IH-001~IH-005、IH-007~IH-014 完成：索引生命周期与空索引回退、FTS 路径键重构、DPAPI/Keychain/受保护文件凭据体系、生产链路 Ed25519 验签 + macOS codesign + 插件沙箱声明强制、安装事务化回滚、Evaluator 死锁防护桥、ImageSharp 图片解码 + 视频缩略图、多窗口隔离测试、真实 SFTP 集成套件（`SftpIntegrationTests`，CI `remote-integration` 作业 + atmoz/sftp 容器）、headless 双尺寸渲染截图门禁 + 覆盖率门禁（Core 行覆盖率基线 46.6%，阈值 43）。依赖新增：SixLabors.ImageSharp 3.1.12（许可见 ThirdPartyNotices.md）。

---

## 工程约定摘要

> 完整约定见 `docs/architecture/` 下各 ADR 与项目记忆。以下为高频项。

- 解决方案文件为 `OpenShell.slnx`（XML），构建用 `dotnet build OpenShell.slnx`。
- 代码注释用中文（与既有代码库一致）。
- DI 扩展方法模式：`services.AddXxxRuntime()` 在 `src/OpenShell.Core/{Feature}/XxxServiceCollectionExtensions.cs`；CLI（`Program.cs`）与 GUI（`AppBuilder.cs`）两个 host 须注册相同的 Core 服务。
- MS DI 取最后注册——扩展方法若重新注册 `IOperationEngine` 会覆盖手动注册。
- ADR 文档位于 `docs/architecture/ADR-XXXX-topic-name.md`，头部含 Status/Date/Stage/Related/Implementation Status。
- REPL 输入语义：`null` = 退出，`string.Empty` + `WasCancelled=true` = Ctrl+C，非空字符串 = 完整输入。
