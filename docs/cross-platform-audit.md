# 跨平台支持审计报告

- **创建日期**: 2026-08-27
- **审计范围**: FileSystem Provider 路径处理、回收站/归档命令、进程命令、IPC 测试基建、CLI 进程级测试、非交互日志契约
- **关联任务清单**: `docs/cross-platform-tasks.md`
- **触发证据**: CI run 33077583320（PR #1）——ubuntu-latest 与 macos-latest 各 89 个测试失败，windows-latest 全绿；仓库历史上仅有的两次 CI 运行均失败，非 Windows 平台从未通过验证。

## 一、基线与实证

- windows-latest：全部通过（2120 通过 / 2 跳过）。
- ubuntu-latest 与 macos-latest：失败集完全一致（89 个），说明是同一组非 Windows 平台缺陷而非环境差异。
- 失败分布：CLI/脚本 E2E 50 个、FileSystemProvider 单测 21 个、命令集成 17 个、Integration 8 个、GUI 合规 1 个、其他 2 个。
- 本地复现环境：WSL Ubuntu + .NET SDK 10.0.400 + .NET 8.0 运行时；测试输出目录以 `-p:ArtifactsPath` 与 Windows 隔离。

## 二、缺陷

| ID | 严重度 | 缺陷 | 证据与影响 |
|----|--------|------|------------|
| D-700 | P0 | 多处硬编码 Windows 路径分隔符 | `FileSystemProvider.ToFsPath` 无条件 `Replace('/', '\\')`，Linux/macOS 上 cd/ls/cp/rm 等基础命令整体失效（21 单测 + 17 命令集成 + 8 Integration 失败）。同族缺陷：`FileTrashService.ToFsPath`（rm 默认走回收站时找不到文件）、`CompressArchiveCommand`/`ExpandArchiveCommand` 相对路径解析、`MainViewModel` 打开/打开方式/快捷方式的本地路径转换。 |
| D-701 | P0 | CliProcessRunner 只解析 `.exe` 产物 | `ResolveCliExePath` 仅查找 `openshell-cli.exe`；Linux/macOS 构建产物为无扩展名 apphost，50 个 E2E 全部抛 FileNotFoundException。 |
| D-702 | P0 | Unix 进程 API 可靠性缺陷族 | (a) Wait-Process 超时分支用 `Exited` 事件/`WaitForExitAsync`，对 `GetProcessById` 打开的外部进程在 Unix 上抛 `InvalidOperationException` 被静默吞掉，-Timeout 永不报错；(b) Start-Process 读取 `ProcessName` 与极快退出进程（echo）存在竞态；(c) 测试间干扰：Stop-Process 测试按名 `sleep` 终止，误杀并行 Wait 测试的 sleep 60。 |
| D-703 | P1 | IPC Raw 测试为 Windows 专属 | `Raw_NamedPipe_*` 直连 `\\.\pipe\` 原生管道，Unix 上路径超长/非法；按名终止与连接失败断言需按平台适配。另发现测试用 UDS 端点名在 macOS 104 字符上限下贴线。 |
| D-704 | P1 | 非交互模式平台通知污染 stdout | 非 Windows 启动时 "RegistryProvider 未注册" 警告经 Console logger 进入 stdout，击穿 T-621 干净流契约（2 个退出码合规测试失败）。 |
| D-705 | P2 | Clear-Content 创建时间断言为 Windows 专属语义 | .NET 在 Linux 上 `GetCreationTime` 映射 ctime，任何内容修改都会更新；就地截断保持 inode 已是正确语义，测试断言需平台分支。 |

## 三、修复策略与实现

1. 路径分隔符统一改用 `Path.DirectorySeparatorChar`（D-700 五处）。
2. `CliProcessRunner` 按平台解析可执行文件名（D-701）。
3. Wait-Process 改 `HasExited` 50ms 轮询等待（超时/无限两分支统一）；Start-Process 对 `ProcessName` 读取加竞态防御；Stop/Wait 测试改用唯一命名的 sleep 副本，消除按名误杀（D-702）。
4. `Raw_NamedPipe_*` 非 Windows 直接跳过；连接失败断言接受取消/IO/Socket 三类快速失败；测试端点名缩短留出 macOS 路径余量（D-703）。
5. 非交互模式 Console logger 最低级别提到 Error，平台通知仅入结构化日志（D-704）。
6. Clear-Content 创建时间断言限 Windows 分支（D-705）。

## 四、验证记录（T-706）

- Windows：`dotnet test OpenShell.slnx` 2120 通过 / 2 跳过（仅真实 SFTP）/ 0 失败。
- WSL Ubuntu（Linux）：同命令 2120 通过 / 2 跳过 / 0 失败（`-p:ArtifactsPath` 隔离输出目录；Cli/Gui host 固定输出目录与 Windows 共享，跨环境需 `--no-incremental` 强制重建）。
- macOS：本地无环境，以 CI macos-latest 为准。
- 已知注意事项：`artifacts/bin/OpenShell/{cfg}` 为 Cli/Gui host 固定共享输出目录，Windows/Linux 交替构建后首次运行 E2E 前需强制重建对应平台的 host（增量检查会因跨环境时间戳误判）。

## 五、完成标准达成情况

- ✅ 本地（Windows + Linux）全量测试全绿，仅保留 2 个真实 SFTP Skip。
- ✅ CI 三平台（含 macOS）全绿——run 33083253004：ubuntu 1m59s / macos 2m37s / windows 4m17s，仓库历史首次。
