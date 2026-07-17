# ADR-0027: GUI 主题与快捷键系统

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0013 (MVVM), ADR-0022 (配置), ADR-0009 (补全)

## Context

ADR-0013 提到主题与命令面板但未细化。完整需求：

1. **主题**：Light / Dark / System / 自定义；不只颜色，还含字体、间距、图标
2. **快捷键**：全局绑定（如 Ctrl+C 复制、F5 刷新、Ctrl+Shift+P 命令面板）
3. **冲突解决**：用户自定义覆盖内置，检测冲突
4. **命令面板**：Ctrl+Shift+P 弹出，模糊搜索命令，回车执行
5. **上下文快捷键**：不同焦点（树/列表/路径栏）不同快捷键集
6. **快捷键持久化**：用户自定义跨会话保留
7. **可发现性**：菜单项显示快捷键、`?` 显示当前上下文快捷键
8. **无障碍**：键盘导航完整性，快捷键不依赖鼠标
9. **跨平台差异**：Mac 用 Cmd 而非 Ctrl

VS Code 的 `keybindings.json` + Command Palette 是参考范本。

## Decision

### 1. 主题系统

#### 主题结构

```csharp
public sealed record Theme(
    string Name,
    ThemeMode Mode,
    ThemeColors Colors,
    ThemeTypography Typography,
    ThemeMetrics Metrics,
    IReadOnlyDictionary<string, string>? IconOverrides = null);

public sealed record ThemeColors(
    string Background,
    string Foreground,
    string Accent,
    string AccentForeground,
    string Border,
    string Muted,
    string Error,
    string Warning,
    string Success,
    string DirectoryItem,
    string FileItem,
    string SelectedBackground);

public sealed record ThemeTypography(
    string FontFamily,
    int FontSize,
    int LineHeight);

public sealed record ThemeMetrics(
    int SpacingUnit,
    int BorderRadius,
    int IconSize);
```

#### 内置主题

- `light` — 浅色（Fluent Light 衍生）
- `dark` — 深色（Fluent Dark 衍生）
- `system` — 跟随 OS（监听 OS 主题变化）
- `high-contrast` — 高对比度（无障碍）

#### 主题文件

`~/.openshell/themes/<name>.toml`：

```toml
name = "solarized-dark"
mode = "dark"

[colors]
background = "#002b36"
foreground = "#839496"
accent = "#268bd2"
error = "#dc322f"
success = "#859900"

[typography]
fontFamily = "Inter"
fontSize = 14

[metrics]
spacingUnit = 8
borderRadius = 4
```

#### 主题加载与切换

```csharp
public interface IThemeService
{
    Theme Current { get; }
    IReadOnlyList<Theme> Available { get; }
    void Apply(Theme theme);
    void Apply(string name);
    IObservable<Theme> Changed { get; }
}
```

实现：
- 启动时加载 `themes/` 目录 + 内置主题
- `set-theme <name>` 命令切换
- 通过 Avalonia `Application.Styles` 切换 `FluentTheme` + 自定义 `StyleDictionaries`
- System 模式监听 `SystemPreferences.UserAccentColor` / `OSThemeChanged` 事件

### 2. 快捷键系统

#### 绑定模型

```csharp
public sealed record KeyBinding(
    KeyGesture Gesture,
    string CommandId,                  // 命令全名或自定义 ID
    IReadOnlyDictionary<string, string>? Args = null,
    string? When = null,               // 上下文条件，如 "focus:pane"
    string? Description = null);

public sealed record KeyGesture(
    KeyModifiers Modifiers,
    Key Key);
```

#### 默认快捷键（CLI 兼容 PSReadLine + VS Code 风格）

| 快捷键 | 命令 | 上下文 |
|---|---|---|
| Ctrl+C | 取消 / 复制（选中时） | 全局 |
| Ctrl+V | 粘贴 | 全局 |
| Ctrl+X | 剪切 | 全局 |
| Ctrl+A | 全选 | 列表 |
| Ctrl+Z | undo | 全局 |
| Ctrl+Y | redo | 全局 |
| F5 | refresh | Pane |
| Backspace / Alt+↑ | NavigateUp | Pane |
| Alt+← | NavigateBack | Pane |
| Alt+→ | NavigateForward | Pane |
| Ctrl+T | NewTab | 全局 |
| Ctrl+W | CloseTab | 全局 |
| Ctrl+Tab | NextTab | 全局 |
| Ctrl+Shift+Tab | PrevTab | 全局 |
| Ctrl+L | ClearHost（CLI）/ FocusLocationBox（GUI） | 全局 |
| Ctrl+Shift+P | ShowCommandPalette | 全局 |
| F1 | Help | 全局 |
| F2 | Rename | 列表 |
| Delete | RemoveItem | 列表 |
| Enter | Open（双击/Enter 进入目录或打开文件） | 列表 |
| Space | QuickPreview | 列表 |
| Ctrl+Shift+N | NewFolder | Pane |
| Ctrl+H | ToggleHiddenFiles | Pane |

#### 跨平台修饰键

Mac 用 `Cmd` 替代 `Ctrl`：

```csharp
public static class KeyGestures
{
    public static KeyModifiers PrimaryModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
}
```

`PrimaryModifier + C` 在 Win/Linux 是 `Ctrl+C`，Mac 是 `Cmd+C`。

#### 用户自定义

`~/.openshell/keybindings.toml`：

```toml
[[binding]]
gesture = "Ctrl+Shift+F"
command = "format-table"
when = "focus:pane"

[[binding]]
gesture = "F9"
command = "open-external"
args = { app = "code" }
```

#### 冲突解决

加载时检测：

- 同 `Gesture + When` 冲突 → 后加载的覆盖，warning
- 用户自定义 > 内置默认
- `unbind <gesture>` 移除内置绑定

#### 上下文条件 `When`

```csharp
public sealed class KeyBindingContext
{
    public string FocusedElement { get; set; } = "";    // pane/tree/locationbox/console
    public string SelectedItemType { get; set; } = "";  // file/directory/archive
    public string CurrentProvider { get; set; } = "";
    public bool IsModalOpen { get; set; }
}
```

`When` 表达式：`focus:pane && provider:fs`，由简单解析器评估。

### 3. 命令面板（Ctrl+Shift+P）

VS Code 风格：

- 中央浮层弹窗
- 输入框模糊搜索命令
- 列表显示命令 `FullName` + `Synopsis`
- 回车执行
- 支持参数（`> get-childitem -r` 直接执行）
- 复用 `ICompletionSource`（ADR-0009）的 CommandNameCompletionSource

```csharp
public sealed class CommandPaletteViewModel : ReactiveViewModel
{
    public string Query { get; set; } = "";
    public IReadOnlyList<CommandPaletteItem> Matches { get; }
    public ReactiveCommand<Unit, Unit> Execute { get; }
}

public sealed record CommandPaletteItem(
    CommandDescriptor Descriptor,
    int Score,                        // 模糊匹配分数
    string HighlightedLabel);         // 高亮匹配字符
```

模糊匹配算法：基于 `fzf` 风格的子序列评分。

### 4. 快捷键可发现性

- 菜单项右侧显示快捷键（如 "Refresh (F5)"）
- `?` 键（无修饰）显示当前上下文所有快捷键的浮层
- 命令面板每条命令显示快捷键

### 5. 无障碍（a11y）

- 所有快捷键必须有等价的菜单 / 命令入口（鼠标可达）
- 焦点视觉指示明显
- 屏幕阅读器（Narrator / VoiceOver / Orca）友好：AutomationProperties.Name
- 高对比度主题完整支持
- 禁用纯鼠标操作

## Alternatives Considered

1. **Avalonia 内置 Command（RoutedCommand）**：被否决，跨 ViewModel 难，与 ReactiveUI 集成弱
2. **每 ViewModel 各自定义快捷键**：被否决，全局冲突无解
3. **仅内置快捷键，不支持自定义**：被否决，用户体验差
4. **完全跟随 OS 快捷键**：被否决，跨平台差异大（Mac vs Win）
5. **不实现命令面板**：被否决，命令清单长时用户难发现

## Consequences

### 优势
- 主题可定制
- 快捷键全局一致 + 可自定义
- 命令面板提升可发现性
- 无障碍支持
- Mac / Win / Linux 统一抽象

### 代价
- 主题系统维护成本（颜色 / 字体 / 图标三层）
- 快捷键冲突检测需小心
- 命令面板的模糊匹配实现需调优
- 跨平台修饰键差异测试

### 约束
- 所有快捷键必须有 `PrimaryModifier` 抽象，禁止硬编码 `Ctrl`
- 用户自定义加载失败时降级到内置
- 主题切换不允许丢失用户当前选中状态
- 命令面板必须支持键盘完整导航（↑↓ Enter Esc）
- `When` 表达式必须简单（仅 `&&` `||` `:` 语法），不允许任意代码
- 主题文件解析失败时降级到 `light`，不阻断启动
- 快捷键 binding 必须有 `Description`，用于 `?` 浮层
- `unbind` 必须显式（不允许空 binding 隐式覆盖）
- 命令面板的模糊匹配延迟 < 50ms（命令清单 < 200 项）
- 高对比度主题必须通过 Avalonia HighContrastTheme 集成
- 屏幕阅读器 AutomationProperties.Name 必须对所有交互元素设置
