# 项目稳定性审计报告

- **创建日期**: 2026-07-18
- **审计范围**: Core 运行时、错误模型、Filter DSL、Provider 取消契约、CI SDK、CLI/GUI 宿主烟测
- **关联任务清单**: `docs/project-stability-tasks.md`
- **合规测试**: `tests/OpenShell.IntegrationTests/ProjectStabilityComplianceTests.cs`

---

## 一、基线

- `dotnet build OpenShell.slnx --nologo`: 0 警告 / 0 错误（本机实际使用 .NET SDK 10.0.302）。
- 结构化 VSTest 基线: 2075 通过 / 7 跳过 / 0 失败。
- 7 个跳过项中，3 个明确标注为产品 bug，2 个是可在本机修复的 Provider 取消契约缺口，2 个依赖真实 SFTP 服务。
- GitHub CI 固定安装 .NET 8.0.x；用 SDK 8.0.404 实测构建 `OpenShell.slnx` 失败，错误为 `MSB4068: 无法识别元素 <Solution>`。

## 二、缺陷清单

| ID | 严重度 | 缺陷 | 证据与影响 |
|----|--------|------|------------|
| D-500 | P1 | `InProcessEventBus.Dispose()` 不可重入 | 第二次调用 `_cts.Cancel()` 时抛 `ObjectDisposedException`，与方法注释“可重入”冲突；宿主重复清理会失败。 |
| D-501 | P1 | 参数异常被归类为 `Unknown` | `ErrorRecord.FromException` 未映射 `ArgumentException`，导致错误类别和退出码失真。 |
| D-502 | P1 | ISO 日期字面量无法词法化 | `Next()` 对数字开头输入直接调用 `LexNumber`，而日期探测只存在于不可能接收 `YYYY-MM-DD` 的 `LexIdentifier`。 |
| D-503 | P1 | Provider 取消合约测试参数无效且断言不完整 | 基类对 `ItemPath` 等参数传 `default`，并且未明确断言每个异步方法必须抛取消异常，导致测试被跳过或假阳性。 |
| D-504 | P1 | FileSystem/SFTP 多个异步入口忽略预取消 token | 两个 Provider 在路径解析、文件 I/O 或凭据查询前未统一调用 `ThrowIfCancellationRequested()`。 |
| D-505 | P0 | CI 使用不支持 `.slnx` 的 SDK | `.github/workflows/ci.yml` 安装 .NET 8.0.x；SDK 8.0.404 本地复现 `MSB4068`，主 CI 无法进入编译阶段。 |
| D-506 | P0 | GUI 直接显示 i18n 资源键 | 真实窗口截图中标题、搜索框、列头和状态栏显示 `gui.*`；XAML 重构后 `MainWindow` 未调用翻译流程，且使用了 `gui.list.*`、`gui.search.placeholder` 等不存在键。 |
| D-507 | P0 | GUI 文件列表未绑定活动标签页 | App 在窗口挂载前设置 `DataContext`，此时 `_mainFileListView` 尚未由 `WireUpControls()` 赋值；挂载后未补绑，导致状态栏有项目计数但列表空白，并出现错误状态条。 |

## 三、修复策略

1. 为每个缺陷先建立跳过的合规测试，再移除对应 `Skip` 验证修复。
2. Core 修复保持局部：Dispose 用原子状态门；错误映射覆盖 `ArgumentException`；Lexer 在数字分支前识别完整 ISO 日期 token。
3. Provider 合约基类为反射调用构造有效参数，并要求每个带取消 token 的异步 API 对预取消请求抛 `OperationCanceledException`。
4. FileSystem/SFTP 的公共异步入口在任何路径验证、I/O 或连接动作前检查取消。
5. CI SDK 对齐到 .NET 10.0.x，与 `.slnx` 和当前本机构建基线一致。
6. GUI 采用统一的可重复翻译遍历，保留原始资源键以支持运行时切换 locale；窗口挂载后立即同步活动标签页到文件列表。

## 四、明确边界

- `SftpProviderContractTests.GetItemAsync_Nonexistent_ReturnsNull` 与 `GetChildrenAsync_Nonexistent_ReturnsEmpty` 必须连接真实 SSH/SFTP 服务，本轮保持 `Skip`，后续应以隔离容器建立集成环境。
- GUI 的标签拖出、标签状态持久化和窗口图标属于已记录的未来增强，不属于本轮稳定性修复。
- `NotSupportedException` 中用于明确表达 pipeline-only、平台限制或只读能力的分支不是缺陷，不做无差别替换。

## 五、完成标准

- D-500 至 D-505 均有机械化测试且通过。
- D-506/D-507 的 GUI 合规测试通过，真实窗口不泄漏 `gui.*` 键且文件列表显示当前目录项目。
- 仅保留 2 个真实 SFTP 基础设施 Skip，不再保留 bug/取消契约 Skip。
- `dotnet build OpenShell.slnx` 为 0 警告 / 0 错误。
- 全解决方案测试 0 失败，CLI 命令执行与 GUI 启动烟测通过。

## 六、最终验收记录（T-591，2026-08-27）

- `dotnet build OpenShell.slnx --nologo`：0 警告 / 0 错误（含 NuGet 审计；此前阻塞项见 gui-cli-optimization 主题 D-626 SSH.NET 升级）。
- 全解决方案测试（连跑两遍）：**2119 通过 / 2 跳过 / 0 失败**；跳过项仅为两个真实 SFTP 基础设施测试（符合「明确边界」）。
- CLI 真实进程烟测：`--version` / `--help` 退出码 0；未知参数退出码 3 且 stderr 给出 usage；`-Command` 成功退出码 0、命令未找到退出码 4；`-File` 脚本执行退出码 0。
- GUI 烟测：`dotnet run --project src/OpenShell.Gui.Host` 启动，窗口进程稳定运行 12 秒无异常后正常终止。
- 验收中发现并修复的新缺陷均已回写 `docs/gui-cli-optimization-audit.md`（D-626/D-627/D-628，对应 T-623/T-624/T-625）。
