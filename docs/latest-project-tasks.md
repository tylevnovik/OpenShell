# 最新项目可用性审计任务清单

对应审计：[docs/latest-project-audit.md](latest-project-audit.md)  
建立日期：2026-08-29  
状态说明：本轮审计已完成，LP-001～LP-010 均已实现并完成当前回归。`LatestProjectComplianceTests.cs` 已建立为真实行为基线，当前 11 项新增测试均无 Skip。

| ID | 优先级 | 任务 | 依赖 | 对应验证 | 状态 |
|---|---:|---|---|---|---|
| LP-001 | P0 | CLI 在构造 Args 前校验 Mandatory、未知参数、重复参数、缺失值，统一返回 InvalidArgument/稳定退出码；破坏性命令不得隐式把当前目录当目标 | - | 必填参数、未知参数、无参文件命令进程级测试 | [x] |
| LP-002 | P0 | 将 FileListView 的 UI 多选同步到 ActivePane.SelectedItems，覆盖普通选择、Ctrl/Shift 多选、右键选择、切 tab 后清理 | LP-001 无关 | Avalonia Headless 真实 SelectionChanged + 文件复制/删除状态测试 | [x] |
| LP-003 | P1 | 统一 Property/Value/Path 的 CLI 输出契约，保证 Get-Date、Convert-*、Get-Location 等输出实际值并支持管道 | - | CLI 输出快照与结构化值测试 | [x] |
| LP-004 | P1 | 接通 PreviewPane：控件布局、选中项订阅、文本/图片/目录等最小预览能力和菜单状态 | LP-002 | GUI headless 控件 + 选中文件预览测试；真实尺寸截图保留为人工边界 | [x] |
| LP-005 | P1 | 接通 Ctrl+L/Alt+D、Enter/Escape 地址栏编辑，以及 Ctrl+F/Ctrl+Shift+F/Space/Ctrl+Z/Ctrl+Y/Shift+Delete 快捷键 | LP-002 | 真实 KeyDown/焦点/命令效果测试 | [x] |
| LP-006 | P1 | 在文件列表注册拖放目标，接入 Pointer 拖出与 DragOver/Drop 拖入，验证 copy/move/delete 效果 | LP-002 | Avalonia 拖放属性、Pointer 源和命令分发接线 | [x] |
| LP-007 | P1 | 用 IDriveRegistry、Favorites、Recent 和真实 Provider 路径构建导航树；所有节点点击都必须可导航 | - | 导航节点路径和实际 Navigate 测试 | [x] |
| LP-008 | P1 | 将 ViewMode 绑定到实际模板/布局；接通四个视图菜单项和 New Window 菜单项 | - | 菜单点击后布局/窗口行为测试 | [x] |
| LP-009 | P1 | 把当前 GUI/CLI 合规测试从“字段/方法存在”提升为真实控件事件、命令副作用和进程输出断言 | LP-001~LP-008 分批 | `LatestProjectComplianceTests` 11 项真实行为测试 | [x] |
| LP-010 | P2 | 清理并更新过时的 gui-host/optimization 审计结论，明确哪些是已实现、已接线、仅有服务层、仅人工验证 | LP-009 | 本文与两份历史审计追加当前状态/边界 | [x] |

## 推荐修复顺序

1. LP-001：先消除 CLI 错误输入和破坏性默认行为。
2. LP-002：恢复 GUI 文件管理器最核心的选择状态链路。
3. LP-003：修正命令结果可见性，避免“成功但无输出”。
4. LP-004~LP-008：逐项接通 GUI 功能，并为每项补真实交互测试。
5. LP-009~LP-010：最后收紧测试和文档口径，重新执行构建、全量测试、CLI 黑盒矩阵与人工 GUI 双尺寸验收。

## 本轮变更日志

- 2026-08-29 LP-001/LP-003 完成：新增共享 `CommandArgumentBinder`，CLI、Pipeline、GUI Host 和 AST 路径统一校验未知/重复/缺失参数；Property 输出显示 `Value`；新增 5 项进程级合规测试。
- 2026-08-29 LP-002/LP-004~LP-008 完成：接通文件列表选择、PreviewPane、地址栏与快捷键、拖拽、动态导航、ViewMode 和 New Window；全局搜索/预览异步 UI 更新回到 UI 线程；新增 6 项 Avalonia 行为测试。
- 2026-08-29 LP-009 完成：新增测试不再只验证字段/方法存在，而是触发选择、快捷键、预览、菜单与控件状态；CLI 黑盒参数矩阵通过。
- 2026-08-29 LP-010 完成：本审计及 `gui-host`、`gui-cli-optimization` 历史审计均追加当前修复结论；真实桌面截图仍明确标记为人工验证边界。
- 2026-08-29 回归：`dotnet build OpenShell.slnx --no-restore` 为 0 警告/0 错误；全解方案 2131 通过 / 2 跳过 / 0 失败。
