# GUI 与 CLI 产品化优化审计报告

- **创建日期**: 2026-07-18
- **审计范围**: Avalonia 主窗口、主题与状态、标签与会话、CLI 顶层参数、输出流、退出码、启动生命周期
- **关联设计**: `docs/plans/2026-07-18-gui-cli-optimization-design.md`
- **关联任务清单**: `docs/gui-cli-optimization-tasks.md`
- **合规测试**: `GuiCliOptimizationGuiComplianceTests.cs` / `GuiCliOptimizationCliComplianceTests.cs`

## 一、基线与实证

- 当前分支建立前，全解决方案为 2088 通过 / 2 跳过 / 0 失败；两个跳过项均依赖真实 SFTP 服务。
- 真实 1200x800 GUI 截图显示：Dark 主题下状态栏仍为浅灰色；详情面板不可见但保留约 250px 黑色空列；文件区未占满内容宽度；工具栏混用字符、emoji 和文本；活动标签缺少视觉层级。
- `App.axaml` 只加载 `FluentTheme`，`Styles/Colors.axaml`、`Controls.axaml`、`Icons.axaml` 未显式合并。多个 View 仍含 `LightGray`、`Red`、`#1E1E1E` 等硬编码颜色。
- 真实 CLI 探针显示：`--help`、`--version`、缺少值的 `--command` 均被忽略并进入 REPL；help/version 输出包含 Generic Host info 日志和 banner。不存在的 `--file` 返回 1，但缺少统一 usage。
- 新增合规基线：GUI 1 通过 / 8 跳过 / 0 失败，CLI 1 通过 / 5 跳过 / 0 失败；所有跳过项均标注对应 T-610~T-622。

## 二、GUI 缺陷

| ID | 严重度 | 缺陷 | 证据与影响 |
|----|--------|------|------------|
| D-600 | P0 | 语义主题资源未接入，View 存在硬编码颜色 | Dark 截图中状态栏为浅灰；主题切换只能覆盖部分控件，焦点、错误和控制台颜色不一致。 |
| D-601 | P0 | 隐藏详情面板仍占固定列宽 | `MainWindow.axaml` 第五列固定 `Width="250"`，`DetailsPane.IsVisible=false` 不会折叠列，主文件区损失约 21% 宽度。 |
| D-602 | P1 | 文件区状态与列布局不完整 | 文件列表只有加载条和错误条，没有可识别的空目录/过滤无结果/重试状态；头部与行列宽使用不同定义。 |
| D-603 | P1 | 命令栏与标签栏不符合成熟桌面控件习惯 | 导航按钮使用 Unicode 字符，其他操作混用 emoji/文本；标签关闭为字母 `x`，无活动态和新建标签入口。 |
| D-604 | P1 | 状态栏信息与主题层级失真 | `SelectedSizeLabel` 已在 ViewModel 中计算但未显示；状态栏硬编码 `LightGray`，错误按钮在无错误时仍占显著位置。 |
| D-605 | P1 | 详情面板未完成 i18n 与空选择处理 | 六个字段标签硬编码中文，绑定直接索引 `SelectedItems[0]`，无选择时没有明确空状态。 |
| D-606 | P1 | 标签会话服务已实现但未接入主窗口 | `SessionTabsService` 已注册到 DI，但 `App`/`MainViewModel` 未加载或更新它；重启后标签不会恢复。 |
| D-607 | P2 | 可访问性和响应式约束缺少机械验证 | 图标按钮缺少统一可访问名称/焦点态；800px 最小宽度下命令栏可能溢出；现有测试多为字段存在性检查。 |

## 三、CLI 缺陷

| ID | 严重度 | 缺陷 | 证据与影响 |
|----|--------|------|------------|
| D-620 | P0 | help/version 顶层契约缺失 | `--help`、`--version` 被参数循环忽略，随后启动完整 Host 和 REPL，无法用于脚本探测。 |
| D-621 | P0 | 参数错误被静默接受 | 未知参数、缺失值以及 `--command`/`--file` 互斥组合没有验证，可能意外进入交互模式。 |
| D-622 | P0 | 参数解析晚于 Host 和后台服务启动 | 会话自动保存、审计保留、插件扫描和 Generic Host 日志在确定调用模式前已启动，污染 help/version 并增加副作用。 |
| D-623 | P1 | 非交互退出码丢失错误类别 | `RunCommandAsync` 对所有错误返回 1，对取消返回 0，与 ADR-0026 和 `ExitCodes.For` 冲突。 |
| D-624 | P1 | stdout/stderr 与编码契约不完整 | help/version 包含 info 日志和 banner；未显式设置 UTF-8；重定向消费方无法稳定解析输出。 |
| D-625 | P2 | 顶层调用逻辑集中在 2223 行 `Program.cs` | 参数探测、DI、会话、插件和运行模式耦合，导致新增参数容易出现两套不一致判断。 |

## 四、修复策略

1. GUI 先接入语义 token，再修复工作区列折叠、工具栏/标签、文件状态、状态栏与详情面板，最后接入标签会话和无障碍验证。
2. CLI 新增无副作用的早期参数解析器和 usage 渲染器；help/version/错误在构建 Host 前完成。
3. 非交互日志默认静默，命令结果只写 stdout，错误只写 stderr；退出码统一使用 ADR-0026。
4. 每项实现前移除对应合规测试的 `Skip` 观察失败，实现后定向验证并更新任务清单。

## 五、完成标准

- GUI 在 Light/Dark/System 下颜色一致，详情栏隐藏时不占空间，文件区具备空/加载/错误/过滤状态，标签和高频命令可用且可键盘访问。
- CLI help/version/参数错误不启动 Host，输出流干净，错误类别映射到稳定退出码。
- 真实 GUI 在 1200x800 与 800x500 两种尺寸完成截图复验。
- `dotnet build OpenShell.slnx --nologo` 0 警告 / 0 错误；全解决方案 0 失败，仅保留两个真实 SFTP Skip。
