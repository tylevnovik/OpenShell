# CLI 进程级端到端测试体系任务清单

- **创建日期**: 2026-07-11
- **基准文档**: `docs/cli-e2e-audit.md`（审计）、ADR-0033 §5
- **参考来源**: `C:\Users\blmpt\Downloads\powershell-ref\` Pester 测试方法论
- **验证机制**: `tests/OpenShell.Core.Tests/CliE2E/CliProcessE2EComplianceTests.cs`
- **追踪规范**: 见 `agents.md`「任务追踪规范」

---

## 状态图例

- `[ ]` 未开始
- `[~]` 进行中
- `[x]` 已完成（测试通过）
- `[!]` 阻塞（注明原因）

## 优先级图例

- **P0** 阻断性缺陷（功能不可用）—— 必须最先修
- **P1** 测试体系建立
- **P2** 增强/收尾

---

## 任务总表

| ID | 优先级 | 缺陷 | 描述 | 状态 | 依赖 | 测试 |
|----|--------|------|------|------|------|------|
| T-300 | P0 | D-300/D-301 | CLI 添加 `-Command <string>` 和 `-File <path>` 标志，支持非交互执行 | `[x]` | — | T-310+ |
| T-301 | P0 | D-302 | 创建 `CliProcessRunner` C# 测试工具：启动真实 CLI 进程 + 捕获 stdout/stderr/exitcode | `[x]` | T-300 | T-310+ |
| T-302 | P1 | — | 创建 `tests/TestData/Scripts/cli_assets/` fixture 脚本目录 | `[x]` | — | — |
| T-310 | P1 | — | cd/Set-Location 进程级 E2E：`cd ..` / `cd ../sibling` / `cd .` / `cd subdir` / `cd /abs` | `[x]` | T-301 | Compliance §cd |
| T-311 | P1 | — | pwd/Get-Location 进程级 E2E：`pwd` 输出当前路径 | `[x]` | T-301 | Compliance §pwd |
| T-312 | P1 | — | ls/Get-ChildItem 进程级 E2E：`ls` 列出目录内容 | `[x]` | T-301 | Compliance §ls |
| T-313 | P1 | D-317/D-323 | mkdir/New-Item 进程级 E2E：`mkdir dir` 创建目录 | `[x]` | T-301 | Compliance §mkdir |
| T-314 | P1 | D-318/D-319 | rm/Remove-Item 进程级 E2E：`rm file` 删除文件 + `rm -r dir` 递归删除 | `[x]` | T-301 | Compliance §rm |
| T-315 | P1 | — | cp/Copy-Item 进程级 E2E：`cp src dst` 复制文件 | `[x]` | T-301 | Compliance §cp |
| T-316 | P1 | — | mv/Move-Item 进程级 E2E：`mv src dst` 移动文件 | `[x]` | T-301 | Compliance §mv |
| T-317 | P1 | — | cat/Get-Content 进程级 E2E：`cat file` 读取文件内容到 stdout | `[x]` | T-301 | Compliance §cat |
| T-318 | P1 | D-322 | echo/Set-Content 进程级 E2E：写内容到文件（含 `>` 重定向） | `[x]` | T-301 | Compliance §echo |
| T-320 | P2 | — | 管道进程级 E2E：`ls \| measure` 等管道操作 | `[x]` | T-301 | Compliance §pipe |
| T-321 | P2 | — | 错误处理进程级 E2E：不存在的命令 → exitcode != 0 + stderr 非空 | `[x]` | T-301 | Compliance §error |
| T-330 | P2 | D-320/D-321/D-322/D-323 | `-File` 脚本文件执行 E2E：`openshell-cli -File cli_assets/filesystem_ops.osh` | `[x]` | T-302, T-300 | Compliance §file |
| T-331 | P2 | D-320/D-321/D-323 | `-File` 多语句脚本执行 E2E：cd_navigation.osh 含 cd + pwd 序列 | `[x]` | T-302, T-300 | Compliance §file |
| T-399 | P3 | — | CliProcessE2EComplianceTests 合规测试套件建立 | `[x]` | — | — |

---

## 修复执行顺序

### 第 0 批：建立验证基线
- T-399 建立 `CliProcessE2EComplianceTests`，所有测试用 `[Fact(Skip="pending T-XXX")]` 标注。

### 第 1 批：P0 前置（CLI 标志 + 测试基础设施）
1. T-300 添加 `-Command`/`-File` 标志
2. T-301 创建 `CliProcessRunner`

### 第 2 批：P1 核心命令 E2E
1. T-302 创建 fixture 脚本目录
2. T-310 cd / T-311 pwd / T-312 ls
3. T-313 mkdir / T-314 rm
4. T-315 cp / T-316 mv
5. T-317 cat / T-318 echo

### 第 3 批：P2 管道 + 错误 + 脚本文件
1. T-320 管道
2. T-321 错误处理
3. T-330/T-331 `-File` 脚本执行

### 第 4 批：P3 收尾
移除所有 Skip，确保合规测试套件全绿。

---

## 完成判定标准

CLI 进程级 E2E 测试体系完成须同时满足：
1. `CliProcessE2EComplianceTests` 全部用例通过（无 Skip）。
2. `dotnet build OpenShell.slnx` 0 警告 0 错误。
3. 全解决方案测试套件全绿（不引入回归）。
4. 本文件所有任务 `[x]`。
5. `openshell-cli.exe -Command "..."` 和 `-File <path>` 可正常执行。
6. `cd ..` 等用户报告的 bug 在进程级测试中被覆盖。

---

## 变更日志

- 2026-07-11 创建任务清单与审计文档（`docs/cli-e2e-audit.md`）。审计发现 P0 缺陷 D-300（CLI 缺 -Command）、D-301（缺 -File）、D-302（无进程级测试基础设施）。
- 2026-07-11 T-300~T-302 完成：添加 `-Command`/`-File` 标志、创建 `CliProcessRunner`、建立 fixture 脚本目录。
- 2026-07-11 T-310~T-321 完成：cd/pwd/ls/mkdir/rm/cp/mv/cat/echo/管道/错误处理进程级 E2E 测试全部通过。修复 D-306~D-316（cd 假目录、Tokenizer DotDot、mkdir 冒号参数、ConfirmPreference 无限循环、cd 点号、dotted 文件名、-File 服务、mkdir 冒号形式、AliasExpander 引号）。
- 2026-07-11 T-330~T-331 完成：`-File` 脚本执行 E2E。修复 D-317~D-323：
  - D-317: `ParseArgs` 不识别 `-name:value` 冒号形式（字符串快路径）。
  - D-318: `DispatchAsync` 的 `StripShouldProcessCommonParams` 无条件覆盖 `ConfirmPreference=High`，导致 rm 无限循环。
  - D-319: `AliasExpander.Expand` 展开参数位置的 token（如 `rm -r dir` 中 `dir` 被展开为 `get-childitem`）。
  - D-320: AST 路径 `Evaluator.InvokeCommand` 缺别名解析（`-File` 模式脚本中 `mkdir` 无法解析）。
  - D-321: `ParseCommand` 参数循环起始处 `SkipNewLinesAndComments` 消费换行符，导致 `mkdir foo\ncd bar` 被合并为单条命令。
  - D-322: `>` 输出重定向未实现（字符串快路径 + AST 路径均缺失）。
  - D-323: `mkdir`/`touch` 同时注册为 `[Verb]` 别名和 `AliasRegistry` 条目，D-320 仅在 `desc is null` 时查别名，导致 `-type:directory` 默认参数丢失。修复为别名优先于命令注册表。
- 2026-07-11 T-399 完成：合规测试套件 20 通过 / 0 跳过 / 0 失败。全量 2025 通过 / 7 跳过 / 0 失败。
