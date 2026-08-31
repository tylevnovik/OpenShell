# 后续可靠性与安全改进审计

**审计日期**：2026-08-31  
**审计基线**：当前工作树（包含上一轮 LP-001～LP-010 修复）  
**范围**：搜索索引、凭据与安全输入、包/更新/插件信任链、安装事务、远程 E2E、预览能力、异步边界和多窗口生命周期。

## 一、结论

上一轮已修复 CLI 参数错误、GUI 选择同步、预览面板接线、快捷键、拖拽、导航和视图菜单等可用性阻断；本轮收尾后的自动化回归为 2147 通过 / 4 个有明确原因的测试跳过 / 0 失败。

初始复核发现一个会直接导致功能“看起来完全不可用”的缺陷：长期 SQLite 搜索索引被注册但没有发现启动加载或刷新调用，`Search-Global` 检测到索引服务后不会回退到实时枚举；空索引会直接返回空结果。索引的 FTS 同步和路径范围查询也存在正确性缺口。上述问题已在本轮处理，实施证据见第四节。

初始复核同时发现项目仍处于 alpha 发布安全边界：SFTP 凭据明文保存，控制台密码可见，macOS 更新代码签名校验恒真，开发用签名 stub 仍可被直接构造，插件缺少沙箱声明时默认完全信任。其中凭据、控制台输入、macOS 策略和插件 fail-closed 已处理；开发测试 stub、目标平台现场验证及其余未完成项仍按第五节披露。

## 二、问题清单

| ID | 优先级 | 问题 | 证据 | 目标 |
|---|---:|---|---|---|
| IH-001 | P0 | SQLite 索引未接入启动/刷新生命周期，索引路径下全局搜索可能恒为空 | `src/OpenShell.Core/Preview/PreviewServiceCollectionExtensions.cs` 仅注册服务；`GlobalSearchCommand` 检测到服务后直接返回 | 建立索引生命周期、空索引安全回退、路径范围正确 |
| IH-002 | P0 | FTS5 与主表按 `name` 连接，重复文件名会串结果；Upsert/Delete 可能遗留或删除错误 FTS 行 | `src/OpenShell.Core/Preview/FileIndexStore.cs` 的 FTS schema、Upsert、Delete、SearchByName | 用稳定 rowid/路径关联并补重复名、更新、删除测试 |
| IH-003 | P0 | SFTP 密码和私钥口令明文写入 JSON；安全输入使用可见 ReadLine | `src/OpenShell.Providers.Remote/InMemoryCredentialProvider.cs`、`src/OpenShell.Core/Security/ConsoleSecurePasswordPrompter.cs` | 接入 OS 安全存储/回显关闭，并让持久化失败可见 |
| IH-004 | P0 | 更新/插件信任链存在开发占位或默认完全信任 | `NullSignatureVerifier`、`PlatformCodeSignatureVerifier`、`PluginLoader` | 生产路径 fail-closed，平台校验与沙箱默认策略明确 |
| IH-005 | P1 | Provider 安装解压、current 切换、配置保存不是单事务 | `src/OpenShell.Core/Packaging/Installation/ProviderInstaller.cs` | staging + 原子切换 + 失败回滚 |
| IH-006 | P1 | 真实 SFTP 连接测试仍跳过 | `tests/OpenShell.Providers.Remote.Tests/SftpProviderContractTests.cs` | 隔离 SSH/SFTP 测试环境并覆盖连接/取消/重连 |
| IH-007 | P1 | GUI 全局搜索固定不搜索内容 | `src/OpenShell.Gui.Host/ViewModels/GlobalSearchViewModel.cs` 的 `IncludeContents: false` | 增加内容搜索开关、取消、空结果和索引状态反馈 |
| IH-008 | P1 | Evaluator 用同步等待消费异步流 | `src/OpenShell.Core/Runtime/Evaluator.cs` 的 `GetAwaiter().GetResult()` | 异步执行边界或明确无死锁约束并补压力测试 |
| IH-009 | P1 | 图片仅 PNG；PDF 仅轻量文本提取；视频仅元数据 | 各 Previewer 的实现限制说明 | 提供可选解码能力和清晰的降级提示 |
| IH-010 | P2 | 新窗口复用同一个 ViewModel，生命周期和工作区状态不独立 | `src/OpenShell.Gui.Host/Views/MainWindow.axaml.cs` | 独立窗口会话或共享状态引用计数 |
| IH-011 | P2 | 真实桌面双尺寸截图和 GUI 门禁仍缺失 | `docs/gui-cli-optimization-tasks.md` T-630 | 补 1200x800 / 800x500 桌面验收及 CI 门禁 |

## 三、本轮边界

本轮优先完成 IH-001～IH-005 和 IH-007；IH-006 先补测试基础设施与可运行的本地隔离测试入口；IH-008～IH-011 记录并补最小回归，完整跨平台桌面验收仍需目标平台人工执行。

安全实现必须保持开发测试便利性，但测试 stub 不得成为默认生产注册；任何无法验证平台安全能力的路径应拒绝安装/更新或明确要求用户显式确认，不能静默放行。

## 四、本轮实施结果与证据

| ID | 实施结果 | 主要证据 |
|---|---|---|
| IH-001 | 已完成 | `FileIndexLifecycleService` 接入 hosted service；启动加载 SQLite，后台刷新失败时保留实时搜索回退；`GlobalSearchCommandTests` 验证索引搜索和 Path 范围。 |
| IH-002 | 已完成 | FTS5 改为保存 `path` 与 `name` 并按路径关联主表；覆盖重复文件名、改名和删除，`FileIndexStoreTests` 与合规测试通过。 |
| IH-003 | 已完成 | 新增 `ISecretStore`；Windows 使用 DPAPI，Unix 使用 AES-GCM 受保护文件；凭据元数据不再写入密码/口令；交互式密码输入关闭回显，并覆盖持久化失败回滚。 |
| IH-004 | 已完成（目标平台边界见下） | 插件缺失或不匹配声明时 fail-closed；Windows WinTrust 结构布局修复并覆盖无效工件；macOS 增加 `codesign --verify --deep --strict` 策略；CLI 生产注册使用 Ed25519 验签器。 |
| IH-005 | 已完成 | Provider 包先解压到 staging，校验 manifest 名称/版本，再切换安装目录和 current；配置提交失败会恢复旧版本/current，新增回滚测试通过。 |
| IH-007 | 已完成 | GUI 增加内容搜索开关、取消按钮和索引状态文案；CLI/核心内容搜索已用真实 fake provider 路径验证，Avalonia 合规测试覆盖控件和状态属性。 |
| IH-010 | 已完成 | 新窗口从 DI 创建独立 `MainViewModel`；`MultiWindowLifecycleTests` 覆盖独立 DataContext、工作区状态隔离、关窗不破坏其他窗口（3 例全绿）。 |
| IH-006 | 已完成（真实执行在 CI） | `SftpIntegrationTests` 6 例 + `SftpIntegrationFactAttribute` 条件 Skip；CI `remote-integration` 作业挂载 atmoz/sftp 容器（端口 2222，凭据经环境变量注入）。覆盖存在/缺失、目录列举、预取消、8MB 往返哈希一致、故障注入断线重连、错误口令鉴权分类。主机密钥策略沿用 SSH.NET 首次信任，每次运行连接全新容器密钥；密钥固定另立后续任务（IH-015）。 |
| IH-008 | 已完成 | `Evaluator` 新增 `BlockSafe` 同步桥：无同步上下文（CLI）零开销直等；有上下文（GUI）时整体搬到线程池等待，延续不再捕获被阻塞上下文。合规测试用"从不泵送"的假上下文 + 10s 守卫验证无死锁且零回调入队，Skip 已解除。 |
| IH-009 | 已完成 | 引入 SixLabors.ImageSharp 3.1.12（纯托管，许可见 ThirdPartyNotices.md）：PNG/JPEG/GIF/BMP/WEBP 解码 + 4096px 等比缩略 + 64MB 输入上限；损坏/超限显式 NotSupported。视频经 ffmpeg 提取首帧缩略图并接入 PreviewPane/QuickLook 渲染，ffmpeg 缺失时保持元数据降级。格式矩阵合规测试 8/8 通过，Skip 已解除。 |
| IH-011 | 部分完成（门禁已机械化） | `DesktopVisualAcceptanceTests` 以 headless+Skia 真实渲染 1200x800 / 800x500 并写入 `docs/screenshots/`，每次 CI 复跑；CI 新增覆盖率门禁（Coverlet.msbuild，基线 46.6%，阈值 43）与 GUI 自动化门禁步骤。真实桌面人工复验仍是待办。 |
| IH-012 | 已完成 | macOS 默认改用 `KeychainSecretStore`（/usr/bin/security，带只读可用性探测；CI 无钥匙串会话自动回退受保护文件）；工厂选择逻辑 6 例测试覆盖。 |
| IH-013 | 已完成 | CLI 启动时 `LastPersistenceError` 非空即向 stderr 输出告警；损坏凭据文件回归测试通过。 |
| IH-014 | 已完成 | AGENTS.md 已登记本主题。 |

## 五、未完成项与验证边界（第二轮后更新）

- IH-006 本地未实测容器：本机无 docker、WSL 安装 sshd 需密码，集成用例真实执行依赖 CI `remote-integration` 作业；本地已验证条件 Skip 与编译。若 CI 首次运行暴露 atmoz/sftp 行为差异（如 `/upload` 路径），需按失败日志微调环境变量。
- IH-011 剩余人工步骤：三平台真实桌面 1200x800 / 800x500 复验仍未做；`docs/screenshots/` 中的产物是 headless+Skia 渲染（真实布局/绘制，但无桌面合成器，且测试环境未注入 i18n，界面标签显示键名）。
- IH-015（新）：SFTP 主机密钥固定（host-key pinning）未实现——当前沿用 SSH.NET 首次信任（TOFU），存在中间人风险；需设计凭据级指纹字段与用户确认流程。
- macOS 的 `codesign` 命令仍未在 macOS 主机现场执行（实现为绝对路径 + ArgumentList + 取消处理）；Keychain 路径同理依赖系统 `security` 工具行为。
- Evaluator 其余 9 处 `GetAwaiter().GetResult()` 已全部经 `BlockSafe` 保护；把 shell 改为端到端异步执行仍是未来大改，不在本轮范围。
- 最终验证（第二轮）：`dotnet build OpenShell.slnx` 0 警告/0 错误；全解决方案测试 **2161 通过、8 跳过、0 失败**（跳过 = 2 个旧契约测试需真实服务器 + 6 个 `SftpIntegrationTests` 条件集成用例）。
