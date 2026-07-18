# OpenShell GUI 与 CLI 产品化优化设计

- **日期**: 2026-07-18
- **状态**: 已确认
- **设计方向**: Windows 11 Explorer 式 GUI + PowerShell 式 CLI
- **关联 ADR**: ADR-0008、ADR-0013、ADR-0025、ADR-0026、ADR-0027、ADR-0028、ADR-0034、ADR-0035

## 一、目标与边界

本轮把 OpenShell 从“功能已接线”推进到“可长期使用的桌面文件工作台和命令行工具”。GUI 保留 Explorer 的信息架构与操作密度，强化 OpenShell 的 Provider、命令面板、控制台和任务中心能力；CLI 保留现有命令语义，补齐成熟命令行程序必须具备的启动参数、帮助、版本、错误流和退出码契约。

本轮不重写 Core 命令系统、Parser 或 Provider，不引入新的 UI 框架，不制作营销式品牌界面，不实现标签拖出窗口和远程 SFTP 测试环境。现有 Avalonia + ReactiveUI、i18n、主题、会话服务和命令注册表继续作为基础。

## 二、GUI 信息架构与视觉系统

主窗口采用稳定的五层结构：标签栏、地址与搜索栏、命令栏、内容工作区、状态栏。内容工作区由可调宽导航栏、可伸缩文件区和按需展开的详情栏组成；详情栏隐藏时列宽必须归零，文件区占满剩余空间。标签需要明确的活动态、关闭按钮和新建入口；命令栏只保留高频操作，熟悉操作使用矢量图标和工具提示，文本仅用于“新建”等需要消歧的命令。

视觉资源统一为语义 token：窗口、侧栏、内容、浮层四级表面；主/次文字；边框、悬停、选中、焦点、强调、成功、警告、危险颜色；4px/6px 圆角；4/8/12/16/24px 间距；32px 紧凑控件和 36px 主要控件。Light/Dark/System 三种主题必须共享同一套语义键，禁止 View 中出现主题专用硬编码颜色。字体沿用 Inter，并保持桌面工具所需的紧凑字号与普通字距。

## 三、GUI 交互与状态

默认路径围绕浏览、筛选、选择、复制/移动、重命名、删除和预览展开。每个命令必须有可用态、禁用态、键盘入口和完成反馈。文件区分别呈现加载、空目录、过滤无结果和错误状态；错误状态提供重试，不以红色半透明条长期占据内容。状态栏显示项目数、选中数与选中大小，并让任务和错误入口具有明确层级。

键盘行为继续兼容 Explorer：`Ctrl+L`/`Alt+D` 地址栏、`Ctrl+F` 筛选、`Ctrl+T/W` 标签、`F2` 重命名、`Delete` 删除、`Space` 预览、`Alt+Left/Right` 导航。所有图标按钮提供可访问名称和工具提示；焦点环在明暗主题下均可见；纯装饰元素不进入 Tab 顺序；最低文本对比度以 WCAG AA 为目标。

会话恢复复用 `SessionTabsService`，启动恢复标签路径和活动标签，标签新增、关闭、切换与导航后防抖保存。窗口尺寸、主题和列表排序继续走现有配置服务。

## 四、CLI 调用契约

CLI 在创建 Host 和启动后台服务之前解析参数。顶层支持：`-h|--help|-?`、`-v|--version`、`-c|--command <text>`、`-f|--file <path>`、`--noprofile`、`--profile <path>`、`--session <name>`、`--ipc-server`、`--execution-policy <level>`。未知参数、缺失值、互斥的 `--command/--file` 和非法执行策略必须写入 stderr，附一行帮助提示，并返回 `InvalidArgument`。帮助和版本只写 stdout、退出 0，且不得启动会话、插件或后台服务。

默认控制台日志不进入用户 stdout；诊断信息保留在结构化日志设施中。命令结果只写 stdout，用户可操作错误只写 stderr。非交互执行按最后错误类别调用 `ExitCodes.For(...)`，取消返回 `Cancelled`，语法错误与参数错误保持可区分。控制台输入输出统一为 UTF-8；重定向时不输出 ANSI 控制序列。交互 REPL 继续展示 banner、当前位置和提示，但减少启动噪声。

## 五、组件与数据流

CLI 新增独立的 `CliInvocationOptions` 解析器和 `CliUsage` 渲染器；`Program.Main` 只负责早期解析、Host 生命周期和运行模式选择。现有 `CliHost` 继续负责 REPL 与命令调度，避免本轮扩大到执行引擎重构。

GUI 的主题 token 放在 `Styles/Colors.axaml`，通用控件状态放在 `Styles/Controls.axaml`，图标放在 `Styles/Icons.axaml`。`App.axaml` 显式合并资源。主窗口和子控件只引用语义资源；必要的状态转换进入小型 converter 或 ViewModel 属性，不在 code-behind 重新构建控件树。标签会话由 `MainViewModel` 产生快照，`SessionTabsService` 负责持久化。

## 六、错误处理与验证

GUI 导航或文件枚举失败时保留原列表，显示可重试错误状态并写入 `IErrorStream`；空目录与过滤无结果不得混为错误。会话恢复遇到不可访问路径时回退到可用初始目录，并保留可诊断日志。

验证分四层：纯单元测试覆盖 CLI 参数组合与退出码映射；真实进程测试覆盖 help/version/错误流/命令/脚本；Avalonia headless 测试覆盖资源合并、布局折叠、状态和可访问属性；真实窗口截图覆盖 Light/Dark、1200x800 和最小窗口尺寸。最终要求 `dotnet build OpenShell.slnx --nologo` 0 警告 0 错误，全解决方案 0 失败，除既有两个真实 SFTP 基础设施测试外无跳过项。
