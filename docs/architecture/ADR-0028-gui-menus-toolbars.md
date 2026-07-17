# ADR-0028: GUI 上下文菜单与工具栏

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0013 (MVVM), ADR-0023 (命令清单), ADR-0027 (快捷键)

## Context

GUI 需要右键菜单、工具栏、侧边栏：

1. **右键菜单**：按选中类型动态显示（文件 / 目录 / 压缩包 / 注册表键菜单不同）
2. **工具栏**：常用操作按钮（后退/前进/上/复制/粘贴/删除/属性）
3. **侧边栏**：盘符 / 收藏 / 最近访问 / Provider 列表
4. **声明式注册**：命令作者声明菜单项，不写 UI 代码
5. **动态可见性**：菜单项按上下文显示/隐藏（如"在此处打开终端"仅在目录上显示）
6. **Provider 特化**：Registry 右键"导出 .reg"、Archive 右键"解压到此"
7. **子菜单**：嵌套结构（"新建" → "文件夹 / 文件 / 快捷方式"）
8. **图标**：菜单项可含 SVG 图标
9. **分隔符**：分组

VS Code 的 `menus` 贡献点 + `when` 表达式是参考范本。

## Decision

### 1. 菜单贡献点

命令类通过特性声明菜单贡献：

```csharp
[Verb("Copy", Noun = "Item")]
[MenuItem(Path = "context/copy", When = "selected.count > 0", Order = 100)]
[MenuItem(Path = "toolbar/copy", When = "selected.count > 0", Order = 100)]
[Icon("Icons/copy.svg")]
public sealed class CopyItemCommand : ...
```

贡献点类型：

| Path 前缀 | 位置 |
|---|---|
| `context/...` | 右键菜单 |
| `toolbar/...` | 顶部工具栏 |
| `menubar/file/...` | 菜单栏（File 菜单下） |
| `sidebar/drives/...` | 侧边栏驱动器右键 |
| `commandPalette/...` | 命令面板（默认所有命令都进） |

### 2. When 表达式

复用 ADR-0027 的 `KeyBindingContext`：

```csharp
public sealed class MenuContext
{
    public string FocusedElement { get; set; } = "";
    public SelectionInfo Selection { get; set; } = new();
    public ItemPath CurrentLocation { get; set; }
    public string CurrentProvider { get; set; } = "";
}

public sealed class SelectionInfo
{
    public int Count { get; set; }
    public bool AllDirectories { get; set; }
    public bool AllFiles { get; set; }
    public bool ContainsArchive { get; set; }
    public bool SingleItem { get; set; }
}
```

`When` 语法示例：

- `selected.count > 0`
- `selected.count == 1 && selected.allFiles`
- `selected.containsArchive`
- `provider == "reg"`
- `focus == "pane"`

### 3. 菜单树构建

启动时扫描所有命令的 `[MenuItem]`，构建菜单树：

```csharp
public sealed class MenuTree
{
    public MenuNode Root { get; } = new MenuNode("");

    public void Add(MenuItemContribution contribution)
    {
        var segments = contribution.Path.Split('/');
        var node = Root;
        foreach (var seg in segments)
        {
            node = node.Children.FirstOrDefault(c => c.Id == seg)
                     ?? node.AddChild(seg);
        }
        node.Command = contribution;
    }
}

public sealed class MenuNode
{
    public string Id { get; }
    public MenuNode? Parent { get; set; }
    public List<MenuNode> Children { get; } = new();
    public MenuItemContribution? Command { get; set; }
    public bool IsSeparator { get; set; }
    public int Order { get; set; }

    public MenuNode AddChild(string id) { ... }
}
```

### 4. Provider 特化菜单

Provider 程序集声明菜单贡献：

```csharp
// In OpenShell.Providers.Registry
[MenuItem(Path = "context/export", Label = "Export to .reg",
          When = "provider == \"reg\" && selected.count == 1", Order = 200)]
public sealed class ExportRegistryCommand : ICommand<ExportRegistryCommand.Args> { ... }
```

启动时扫描所有 Provider 程序集，菜单贡献注册到主菜单树。

### 5. 内置菜单结构

#### 右键菜单（Pane 选中文件）

```
Open            (Enter)
Open in New Tab
───
Cut             (Ctrl+X)
Copy            (Ctrl+C)
Paste           (Ctrl+V)   [仅目录]
───
Rename          (F2)
Delete          (Del)
───
Properties      (Alt+Enter)
───
Open Terminal Here            [仅目录]
```

#### 工具栏

```
[← Back] [→ Forward] [↑ Up] [↻ Refresh] | [/ Path Box] | [🔍 Search] | [☰ Menu]
```

#### 侧边栏

```
Drives
├── C: (Local Disk)
├── D: (Data)
├── zip::archives.zip
└── s3://my-bucket

Favorites
├── Home
├── Documents
└── Projects

Recent
├── ~/Downloads (5 min ago)
└── ~/.openshell (1 hour ago)

Providers
├── FileSystem
├── Archive
├── Registry
└── Remote
```

### 6. 收藏夹

`~/.opensshell/favorites.toml`：

```toml
[[favorite]]
name = "Projects"
path = "fs::C:/Users/me/Projects"

[[favorite]]
name = "S3 Backup"
path = "s3://my-backup-bucket"
```

- 侧边栏"Add to Favorites"按钮
- 拖拽目录到侧边栏添加
- 右键删除

### 7. 最近访问

`~/.opensshell/recent.jsonl` 自动记录最近 20 个访问路径：

```jsonl
{"path":"fs::C:/Users","ts":"2026-07-07T15:30:00Z"}
```

侧边栏显示前 5 项，全量在 `get-recent` 命令查阅。

### 8. 工具栏命令绑定

工具栏按钮：

```csharp
public sealed class ToolbarViewModel : ReactiveViewModel
{
    public ReactiveCommand<Unit, Unit> Back { get; }
    public ReactiveCommand<Unit, Unit> Forward { get; }
    public ReactiveCommand<Unit, Unit> Up { get; }
    public ReactiveCommand<Unit, Unit> Refresh { get; }

    public ToolbarViewModel(ICommandDispatcher dispatcher, PaneViewModel pane)
    {
        Back = ReactiveCommand.CreateFromTask(() => pane.NavigateBack.Execute());
        // ...
    }
}
```

按钮的 `IsEnabled` 绑定 `ReactiveCommand.CanExecute`（如 Back 在历史栈空时禁用）。

### 9. 菜单项标签国际化

```csharp
[MenuItem(Path = "context/copy", Label = "Copy", LabelKey = "menu.copy")]
```

`LabelKey` 用于 i18n（ADR-0035），`Label` 是默认英文。

### 10. 动态菜单

某些菜单需运行时生成（如"Open With..."列出可用程序）：

```csharp
public interface IDynamicMenuProvider
{
    IReadOnlyList<MenuNode> Generate(MenuContext context);
}

[MenuItem(Path = "context/openWith", IsDynamic = true)]
public sealed class OpenWithMenu : IDynamicMenuProvider
{
    public IReadOnlyList<MenuNode> Generate(MenuContext ctx)
    {
        return DetectPrograms(ctx.Selection).Select(p =>
            new MenuNode(p.Name) { Command = new LazyCommand("invoke-openwith", p) }
        ).ToList();
    }
}
```

### 11. 上下文菜单的 Provider 限制

- Provider 特化菜单仅在对应 Provider 当前位置时显示
- 跨 Provider 选中（如同时选 fs 和 zip 文件，理论上不可，因同一 Pane 通常单 Provider）不支持

### 12. 快捷键集成

菜单项的 `KeyBinding`（ADR-0027）显示在右侧：

```
Copy            Ctrl+C
Rename          F2
```

菜单注册时从 `IKeyBindingRegistry` 查找匹配命令的快捷键。

## Alternatives Considered

1. **代码硬编码菜单**：被否决，Provider 扩展困难
2. **JSON 配置文件**：被否决，与命令代码分离易失同步
3. **Avalonia `Menu` 控件直接用**：被否决，无声明式注册与动态上下文
4. **VS Code `package.json` contributes 风格**：被否决，引入 JSON 解析依赖，特性更直接
5. **不实现工具栏，仅右键菜单**：被否决，常用操作发现性差

## Consequences

### 优势
- 命令作者无需写 UI 代码
- Provider 特化菜单自然扩展
- 上下文动态显示
- 收藏夹 / 最近访问提升体验
- 国际化预留接口

### 代价
- 特性解析与菜单树构建需启动时扫描
- `When` 表达式解析器需维护
- 动态菜单实现复杂
- 侧边栏多视图（Drives/Favorites/Recent/Providers）状态管理

### 约束
- `[MenuItem]` 必须声明 `Order`，无 Order 时按字典序
- `Path` 必须用 `/` 分隔，禁止 `\`
- `When` 表达式失败时菜单项不显示（不报错）
- Provider 特化菜单必须随 Provider 卸载而移除
- 收藏夹路径解析失败时显示但置灰
- 最近访问只记录交互式访问，命令调用不记录
- 动态菜单生成延迟 < 100ms
- 菜单项 `Label` 必须有 `LabelKey` 才能国际化
- 工具栏命令必须可被禁用（`CanExecute`）
- 侧边栏拖拽添加收藏时必须立即持久化
- 工具栏与右键菜单的同一命令贡献必须保持图标一致
