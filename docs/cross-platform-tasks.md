# 跨平台支持任务清单

- **创建日期**: 2026-08-27
- **关联审计**: `docs/cross-platform-audit.md`
- **基线**: 现有失败测试即基线（CI run 33077583320：ubuntu/macos 各 89 失败），无需新增 Skip；修复后对应测试转绿。

状态标记：`[ ]` 待办 / `[~]` 进行中 / `[x]` 完成 / `[!]` 阻塞

| ID | 优先级 | 缺陷 | 任务 | 状态 | 依赖 | 对应测试 |
|----|--------|------|------|------|------|----------|
| T-700 | P0 | - | 建立审计、任务清单与失败基线（CI run 33077583320） | `[x]` | - | CI 三平台矩阵 |
| T-701 | P0 | D-700 | 路径分隔符跨平台化（FS Provider / 回收站 / 归档命令 / GUI 本地路径） | `[x]` | T-700 | FileSystemProviderTests + CommandIntegrationTests + IntegrationTests（unix 转绿） |
| T-702 | P0 | D-701 | CliProcessRunner 按平台解析可执行文件 | `[x]` | T-700 | CliE2E + ScriptE2E 套件（unix 转绿） |
| T-703 | P1 | D-702 | Wait-Process 轮询等待 + Start-Process 竞态防御 + 进程测试唯一名隔离 | `[x]` | T-700 | WaitProcessCommandTests + StopProcessCommandTests + StartProcessCommandTests |
| T-704 | P1 | D-703/D-704/D-705 | IPC Raw 测试平台分支、连接失败断言放宽、非交互 Console 级别、Clear-Content 断言分支 | `[x]` | T-700 | NamedPipeIpcChannelTests + GuiCliOptimizationCliComplianceTests + ClearContentCommandTests |
| T-705 | P0 | - | Windows + WSL 全量验证；CI 三平台终验 | `[~]` | T-701~T-704 | 全解决方案（三平台） |

## 变更日志

- 2026-08-27 T-700 完成：依据 CI run 33077583320 建立审计与失败基线（ubuntu/macos 各 89 失败，失败集一致）；WSL Ubuntu 安装 .NET 10.0.400 作为本地复现环境。
- 2026-08-27 T-701 完成：`ToFsPath`/相对路径解析/本地路径转换共五处改用 `Path.DirectorySeparatorChar`；驱动器根按平台规范化并容错卷标/容量读取。
- 2026-08-27 T-702 完成：`CliProcessRunner` 按平台解析 `openshell-cli(.exe)`，50 个 E2E 在 Linux 恢复运行。
- 2026-08-27 T-703 完成：Wait-Process 改 `HasExited` 轮询（超时/无限分支统一）；Start-Process 对 `ProcessName` 加竞态防御；Stop/Wait 测试改唯一命名 sleep 副本，消除按名误杀的跨测试干扰（该干扰曾使超时测试在全量并行下偶发失败，单独运行通过）。
- 2026-08-27 T-704 完成：`Raw_NamedPipe_*` 限 Windows；连接失败断言接受取消/IO/Socket 三类快速失败；测试端点名缩短留出 macOS 104 字符路径余量；非交互 Console logger 提到 Error 级（平台通知不再污染 stdout）；Clear-Content 创建时间断言限 Windows 分支（Linux 的 `GetCreationTime` 映射 ctime）。
- 2026-08-27 T-705 部分完成：Windows 2120 通过 / 2 跳过 / 0 失败；WSL Ubuntu 同命令 2120 通过 / 2 跳过 / 0 失败；剩余以推送后 CI 三平台（含 macOS）运行为终验。
