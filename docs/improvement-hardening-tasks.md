# 后续可靠性与安全改进任务清单

对应审计：[docs/improvement-hardening-audit.md](improvement-hardening-audit.md)  
建立日期：2026-08-31

| ID | 优先级 | 任务 | 依赖 | 对应验证 | 状态 |
|---|---:|---|---|---|---|
| IH-001 | P0 | 接通 FileIndexStore 启动加载、后台刷新与空索引实时回退；Search-Global 正确应用 Path 范围 | - | 索引生命周期、空索引回退、路径范围 E2E | [x] |
| IH-002 | P0 | 重构 FTS5 与主表关联，修复重复文件名、Upsert 改名、Delete 同步 | IH-001 | FileIndexStore 正确性合规测试 | [x] |
| IH-003 | P0 | 引入凭据存储抽象；Windows DPAPI / Unix 受保护文件；控制台输入关闭回显；持久化失败显式报告 | - | 加密往返、文件无明文、非交互和取消测试 | [x] |
| IH-004 | P0 | 生产安装/更新链路禁止 NullSignatureVerifier；补 macOS 校验策略；插件缺少沙箱声明默认拒绝或受限 | - | 验签失败、平台策略、插件默认权限测试 | [x] |
| IH-005 | P1 | Provider 安装 staging、校验、原子 current 切换、配置失败回滚 | IH-004 | 安装中断/损坏/配置失败回滚测试 | [x] |
| IH-006 | P1 | 加入隔离 OpenSSH/SFTP 测试入口，覆盖真实 GetItem/GetChildren、取消、重连 | - | 远程合规测试；无基础设施时只保留明确 Skip | [x] |
| IH-007 | P1 | GUI 搜索增加内容搜索开关、取消按钮、索引状态、空结果和错误状态 | IH-001 | Avalonia 行为测试 + CLI 搜索内容测试 | [x] |
| IH-008 | P1 | 明确 Evaluator 异步边界，消除 UI 线程同步等待或增加死锁/背压保护 | - | 异步流、取消、UI 同步上下文测试 | [x] |
| IH-009 | P1 | 扩展常见图片格式与安全缩略图；改进 PDF/视频降级体验 | - | 格式矩阵和资源上限测试 | [x] |
| IH-010 | P2 | 新窗口使用独立工作区 ViewModel 或安全共享生命周期 | - | 关闭任一窗口、状态隔离测试 | [x] |
| IH-011 | P2 | 完成真实桌面双尺寸截图和 CI 覆盖率/GUI 门禁 | IH-001~IH-010 | 1200x800 / 800x500 人工验收记录 | [~] |
| IH-012 | P0 | macOS 凭据改用 Keychain 原生存储（Linux 按原建议允许受保护文件），接口可替换 | IH-003 | Keychain 存储选择逻辑与回退测试 | [x] |
| IH-013 | P1 | 启动时凭据库加载错误必须展示给用户（LastPersistenceError 接线） | IH-003 | CLI 启动告警测试 | [x] |
| IH-014 | P2 | AGENTS.md 登记 improvement-hardening 主题 | - | AGENTS.md 当前主题列表 | [x] |
| IH-015 | P1 | SFTP 主机密钥固定（host-key pinning）：凭据级指纹 + 首次连接用户确认，消除 TOFU 中间人风险 | IH-006 | 指纹不匹配拒绝连接测试 | [ ] |

## 变更日志

- 2026-08-31：建立本轮审计和任务清单；先处理搜索索引正确性与生产安全边界。
- 2026-08-31：完成 IH-001～IH-005、IH-007；IH-010 完成独立 ViewModel 实现，真实窗口关闭/状态隔离测试仍待补齐。
- 2026-08-31：第一轮收尾：构建 0 警告/0 错误；全解决方案测试 2147 通过、4 跳过、0 失败。跳过项均有明确基础设施或范围原因，详见审计报告。
- 2026-08-31：第二轮全部落地。IH-006：`SftpIntegrationTests`（6 个真实服务器用例：存在/缺失、列举、取消、8MB 往返、故障注入重连、错误口令鉴权分类）+ CI `remote-integration` 作业（atmoz/sftp 容器）；本地无服务器时条件 Skip，真实执行由 CI 完成（本地仅验证编译与 Skip 行为，未实测容器）。IH-008：Evaluator 增加 `BlockSafe` 同步桥——带同步上下文（GUI）时把等待整体搬到线程池，死锁回归测试（未泵送上下文 + 10s 守卫）通过并解除 Skip。IH-009：引入 SixLabors.ImageSharp 3.1.12（纯托管），图片预览支持 PNG/JPEG/GIF/BMP/WEBP 解码 + 4096px 安全缩略 + 64MB 输入上限，损坏/超限显式 NotSupported；视频经 ffmpeg 提取首帧缩略图（缺失时降级元数据）；格式矩阵合规测试解除 Skip（8/8 通过）。IH-010：`MultiWindowLifecycleTests` 3 例（独立 DataContext、状态隔离、关窗不破坏他窗）。IH-011：headless+Skia 双尺寸渲染门禁（`DesktopVisualAcceptanceTests`，产物 docs/screenshots/desktop-*.png）+ CI 覆盖率门禁（基线 46.6%，阈值 43）+ GUI 自动化门禁步骤；**保留 [~]**：三平台真实桌面人工复验仍未做，且截图为 headless 渲染（测试环境无 i18n 注入，标签显示键名）。IH-012：macOS Keychain 存储（/usr/bin/security，含 CI 无钥匙串会话探测回退）+ 工厂选择测试 6 例。IH-013：CLI 启动输出凭据库加载告警 + 损坏文件回归测试。
- 2026-08-31：第二轮最终验证：`dotnet build OpenShell.slnx` 0 警告/0 错误；全解决方案测试 2161 通过、8 跳过、0 失败（跳过均为需真实服务器的条件集成用例，配置环境变量后即执行）。
- 2026-08-31：配置 origin 并首推 GitHub。CI 首跑暴露三处真实环境问题并全部修复：① atmoz/sftp 容器内 `/upload` 假设不成立（fixture 构造即失败）→ 改为可写目录自动探测（显式 Root → 登录工作目录 → upload/uploads）；② macOS runner 钥匙串真实可用（IH-012 现场生效，凭据实际写入 Keychain），但旧测试 `SetCredentials_PersistsToFile` 断言 `.secrets` 文件存在被打破 → 该测试显式注入 ProtectedFileSecretStore，与平台默认解耦；③ 截图门禁的 headless+Skia 会话与 Gui.Host.Tests 共享静态会话同进程互踩（`IWindowingPlatform` 丢失，随机拖垮并行 [AvaloniaFact]）→ 截图测试迁至独立项目 `tests/OpenShell.Gui.ScreenshotTests`（独立 testhost 进程）+ 程序集内禁并行。修复后本地全量 2161 通过 / 8 跳过 / 0 失败。
- 2026-08-31：修复后 CI 第二轮：三平台矩阵（含覆盖率门禁 43%、GUI/截图门禁）全绿。`remote-integration` 仍红：atmoz/sftp 不会自动创建可写目录（chroot home 为 root 属主，未声明目录则无任何可写位置，探测报 `tried: /, /upload, /uploads`）→ `SFTP_USERS` 按官方语法补 `:1001:1001:upload` 声明可写 upload 目录，fixture 探测逻辑无需改动。
