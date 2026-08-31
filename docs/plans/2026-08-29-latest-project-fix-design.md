# 最新项目可用性修复设计

**目标**：把审计确认的 CLI 参数、对象输出和 GUI 主交互问题修复为可由真实行为测试证明的功能。

**采用方案**：保留现有 Core 服务和命令模型，只补齐断开的边界。CLI 使用一个共享的参数绑定实现，供普通命令和 Pipeline 共用，统一处理未知参数、必填参数、重复参数、转换失败和危险命令的缺省目标。GUI 以 PaneViewModel.SelectedItems 作为唯一选择状态源，由 FileListView 负责同步控件多选；MainWindow 只负责窗口级快捷键和菜单；预览面板复用已注册的 IPreviewService，QuickLookWindow 的渲染逻辑抽成可嵌入控件；拖放仍由现有 AvaloniaDragDropService 承担，只补注册和事件入口。

**数据流**：

CLI：命令行文本 → Tokenizer/分段 → 共享 ArgumentBinder → Args record → 命令执行 → Item/Value 渲染。  
GUI：ListBox/TreeView/快捷键/菜单 → ViewModel 命令 → Core service → Refresh → UI 状态；预览为 SelectedItem → IPreviewService → PreviewPane。

**错误处理**：参数错误在命令实例执行前生成 InvalidArgument，并返回退出码 3；未知参数不再静默丢弃；GUI 没有选中项时命令保持无副作用并显示可理解状态。预览失败显示错误态而不阻塞文件列表，拖放只接受可解析且目标有效的数据。

**测试边界**：新增 CLI 进程级测试验证退出码、stderr、文件状态和对象值；新增 Avalonia Headless 测试真实触发 SelectionChanged、菜单/快捷键、导航节点和预览控件；不以“字段/方法存在”替代行为断言。全量测试、CLI 隔离目录烟测和 GUI 启动/双尺寸人工检查作为交付门槛。

