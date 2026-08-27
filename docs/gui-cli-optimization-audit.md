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
| D-608 | P0 | 文件列表右键菜单命令绑定目标错误 | `FileListView.DataContext` 是 `BrowserTab`，但 XAML 绑定 `#Root.DataContext.CopyCommand` 等仅存在于 `MainViewModel` 的属性；菜单可见但命令为空。 |
| D-609 | P0 | 活动标签切换不通知 `ActivePane` 绑定 | `ActivePane` 使用无通知的自动属性；切换标签只由 code-behind 修补文件列表，面包屑、状态和详情等绑定无法可靠刷新。 |

T-610 已消除 D-600/D-601：生产 `App` 现显式加载语义资源，主题专用颜色集中到 Light/Dark 字典；工作区详情列改为 `Auto`，隐藏时不再保留固定宽度。

T-611 已消除 D-603：工具栏不再使用 Unicode/emoji 充当图标，活动标签状态由 `BrowserTab.IsActive` 驱动，并提供稳定的新建与关闭入口。

T-612 实施中发现 D-608：现有结构测试只验证菜单项存在，未验证 `Command` 实际解析；已新增 T-615 与独立合规测试。

T-615 已消除 D-608：`BrowserTab` 作为文件列表 DataContext 时通过只读 Owner 暴露窗口级命令，菜单不再引用不存在的标签属性。

T-613 实施中发现 D-609：会话恢复会批量切换活动标签，暴露出 `ActivePane` 无属性通知的问题；已新增 T-616 与行为测试。

T-613 已消除 D-606：App 在窗口创建前加载会话，MainViewModel 恢复并持续生成标签快照，退出路径等待保存完成后再释放会话锁。

T-616 已消除 D-609：`ActivePane` 通过 ReactiveUI 属性通知更新，所有依赖活动标签的绑定共享同一状态源。

T-614 已消除 D-607：交互控件具有稳定尺寸、可见焦点态、工具提示和可随 locale 更新的自动化名称；GUI 合规套件已无跳过项。

T-620 已消除 D-620/D-621/D-625：顶层参数由独立纯解析器验证，help/version/usage 无需创建 Host，`Program` 不再维护第二套宽松参数循环。

T-621 已消除 D-622/D-624：用户可见 Console 日志与结构化诊断分离，交互/非交互 stdout 不再混入 Host 生命周期日志，输入输出统一为 UTF-8。

T-622 已消除 D-623：非交互命令和脚本只依据本次错误集返回 ADR-0026 退出码，取消与解析失败不再退化为成功或通用错误。

## 三、CLI 缺陷

| ID | 严重度 | 缺陷 | 证据与影响 |
|----|--------|------|------------|
| D-620 | P0 | help/version 顶层契约缺失 | `--help`、`--version` 被参数循环忽略，随后启动完整 Host 和 REPL，无法用于脚本探测。 |
| D-621 | P0 | 参数错误被静默接受 | 未知参数、缺失值以及 `--command`/`--file` 互斥组合没有验证，可能意外进入交互模式。 |
| D-622 | P0 | 参数解析晚于 Host 和后台服务启动 | 会话自动保存、审计保留、插件扫描和 Generic Host 日志在确定调用模式前已启动，污染 help/version 并增加副作用。 |
| D-623 | P1 | 非交互退出码丢失错误类别 | `RunCommandAsync` 对所有错误返回 1，对取消返回 0，与 ADR-0026 和 `ExitCodes.For` 冲突。 |
| D-624 | P1 | stdout/stderr 与编码契约不完整 | help/version 包含 info 日志和 banner；未显式设置 UTF-8；重定向消费方无法稳定解析输出。 |
| D-625 | P2 | 顶层调用逻辑集中在 2223 行 `Program.cs` | 参数探测、DI、会话、插件和运行模式耦合，导致新增参数容易出现两套不一致判断。 |
| D-626 | P0 | SSH.NET 2025.1.0 高危漏洞导致构建失败 | 2026-08-27 验收扫描发现：NuGet 审计 NU1903（GHSA-q939-rpr3-3284，高危，影响 ≤2025.1.0）被 `TreatWarningsAsErrors` 升级为错误，`dotnet build OpenShell.slnx` 在 main 与当前分支均失败，CI 同样失败。修复版本为 2026.0.0。 |
| D-627 | P0 | 非交互调用参与会话生命周期，污染 stderr 并破坏在途锁 | 2026-08-27 验收扫描发现：`-Command`/`-File` 一次性执行仍会 DetectCrash + LoadOrCreate + AcquireLock + Save + ReleaseLock（`Program.cs` 会话初始化/退出块）。并行非交互进程共享 `default` 锁，互相触发 `[会话] 会话 'default' 可能正在运行…` 写入 stderr，击穿 T-621 干净流契约；全解决方案并行测试 2/2 复现（每次命中不同测试）。且一次性进程会覆盖再删除在途 REPL/GUI 的锁文件，并在退出时用旧快照回写会话 JSON，存在状态覆盖竞态。CLI 宿主从不把会话状态应用回运行时（不恢复路径/历史），非交互执行从会话中无任何收益。 |
| D-628 | P1 | AST 路径把字符串按字符枚举绑定到数组参数 | 2026-08-27 T-630 烟测发现：脚本文件（`-File`）中 `Write-Output "quoted"` / `Write-Output bare` 输出按字符逐项展开（12/13 项），而 `-Command` 字符串快路径输出单项。根因：`Evaluator.ConvertValue` 的数组分支把 `string` 当作 `IEnumerable`（即 `IEnumerable<char>`）逐元素转换；`char[]` 目标除外均应按 PowerShell 语义把字符串包成单元素数组。 |

T-623 已消除 D-626：`Directory.Packages.props` 将 SSH.NET 升级至 2026.0.0（公告修复版本），构建恢复 0 警告 / 0 错误，Remote Provider 套件全绿。

T-624 已消除 D-627：非交互模式（`-Command`/`-File`）完全跳过会话生命周期（崩溃检测、加载、抢锁、保存、释放），交互 REPL 行为不变；新增并行调用与在途锁保护合规测试。

T-625 已消除 D-628：`Evaluator.ConvertValue` 数组绑定对 `string` 值包成单元素数组（仅 `char[]` 目标保留按字符展开）；新增脚本文件 `Write-Output` 单项输出合规测试。

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
