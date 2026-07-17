# ADR-0035: 国际化（i18n）

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: 长期可选
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0022 (配置), ADR-0025 (帮助), ADR-0027 (主题)

## Context

OpenShell 面向多语言用户：

1. **CLI 错误信息**：默认英文，可切换中文 / 日文等
2. **GUI 标签**：菜单项、按钮、状态栏文字
3. **帮助文档**：`about_*` 主题、命令 `--help` 输出
4. **日期 / 数字格式**：按用户 locale
5. **路径 / 文件名**：本地化（用户可见，但内部不变）
6. **错误恢复建议**：本地化
7. **字体回退**：CJK / Arabic 等需要特殊字体

需求约束：

- 不阻塞 M1 实现，可作为整体后续工作
- 用户偏好持久化（ADR-0022）
- 默认英文兜底
- 不强求所有字符串都翻译

## Decision

### 1. i18n 抽象

```csharp
public interface IStringLocalizer
{
    string this[string key] { get; }
    string this[string key, params object[] args] { get; }
}

public interface IStringLocalizerFactory
{
    IStringLocalizer Create(string category);
}
```

### 2. 资源文件

每程序集 `Resources/Strings.{locale}.resx`：

```
OpenShell.Core/
└── Resources/
    ├── Strings.resx                  ← 默认英文
    ├── Strings.zh-CN.resx
    ├── Strings.ja.resx
    └── Strings.de.resx
```

`Strings.resx`：

```xml
<data name="ProviderNotFound" xml:space="preserve">
  <value>Provider '{0}' is not registered.</value>
</data>
```

`Strings.zh-CN.resx`：

```xml
<data name="ProviderNotFound" xml:space="preserve">
  <value>提供者 '{0}' 未注册。</value>
</data>
```

### 3. locale 选择

优先级：

1. `--locale` 启动参数
2. `config.toml` 的 `[i18n] locale = "zh-CN"`
3. `LANG` 环境变量
4. OS 默认 locale
5. 兜底 `en-US`

### 4. locale 检测

```csharp
public sealed class LocaleDetector
{
    public string Detect()
    {
        if (_config.I18n.Locale is { } cfg) return cfg;
        if (Environment.GetEnvironmentVariable("LANG") is { } lang) return ParseUnixLocale(lang);
        if (OperatingSystem.IsWindows()) return WindowsUserLocale();
        return "en-US";
    }
}
```

### 5. 帮助文档多语言

`docs/commands/` 下：

```
docs/commands/
├── get-childitem.md          ← 默认英文
├── get-childitem.zh-CN.md
└── get-childitem.ja.md
```

`get-help` 按当前 locale 选择文件，未找到降级到默认。

`about_*` 主题同样规则：

```
docs/about/
├── about_providers.md
├── about_providers.zh-CN.md
└── ...
```

### 6. 日期 / 数字格式化

`IValueFormatter`（ADR-0011）按 locale：

- 日期：`yyyy-MM-dd` (zh) / `MM/dd/yyyy` (en-US) / `yyyy年MM月dd日` (ja)
- 数字：`1,234.56` (en) / `1.234,56` (de) / `1,234.56` (zh)
- 字节单位：`1.2 KB` 国际通用，但中文环境可用"1.2 千字节"

实现用 `CultureInfo.CurrentCulture`：

```csharp
public sealed class DateTimeFormatter : IValueFormatter
{
    public string Format(object? value, string? formatString)
    {
        if (value is not DateTimeOffset dto) return value?.ToString() ?? "";
        return dto.ToString(formatString ?? "g", CultureInfo.CurrentCulture);
    }
}
```

### 7. GUI 字体回退

不同语言需要不同字体：

- 英文：Inter（默认）
- 中文：思源黑体 / 微软雅黑
- 日文：Noto Sans JP / Hiragino
- Arabic：Noto Sans Arabic

`Theme.Typography.FontFamily` 支持回退列表：

```toml
[typography]
fontFamily = "Inter, 思源黑体, Noto Sans JP, Noto Sans Arabic"
```

Avalonia 字体回退机制自动处理。

### 8. RTL 支持

阿拉伯文 / 希伯来文从右到左：

- Avalonia `FlowDirection.RightToLeft`
- 主题切换时设置
- 路径方向不变（路径是 LTR）

### 9. 翻译工作流

- 资源文件用 `*.resx`，VS / Rider 原生支持
- 提取待翻译字符串到 `messages.pot`（`xgettext` 风格）
- 上传到 Crowdin / Weblate 等翻译平台
- 翻译完成下载 `.po` 转 `.resx`

### 10. 命令名不翻译

- 命令名（`get-childitem`）保持英文
- 命令的 `Description` / `HelpText` 翻译
- 别名可本地化（中文别名"列出"映射到 `get-childitem`，但首期不实现）

### 11. 错误信息本地化

`ErrorRecord.Message` 用 `IStringLocalizer`：

```csharp
public sealed record ErrorRecord(...)
{
    public string LocalizedMessage => _localizer[Message, ...];
}
```

CLI 渲染按 locale，GUI 错误面板同样。

### 12. 性能

- 资源文件加载启动时一次性
- `IStringLocalizer` 内存缓存
- 单次查找 < 0.1ms

### 13. fallback 链

locale `zh-CN` 未找到 key → `zh` → `en-US` → key 本身

### 14. 用户覆盖

`~/.opensshell/locales/zh-CN.json` 用户自定义翻译：

```json
{
  "ProviderNotFound": "找不到 Provider {0}"
}
```

加载时合并：内置 → 用户覆盖。

## Alternatives Considered

1. **仅英文，不本地化**：被否决，国际化用户需求
2. **gettext / po 风格**：被否决，.NET 生态 `.resx` 更原生
3. **JSON 资源文件**：被否决，`.resx` 工具链成熟
4. **运行时翻译服务（Google Translate API）**：被否决，依赖网络与隐私
5. **完整本地化（含命令名翻译）**：被否决，破坏命令可移植性

## Consequences

### 优势
- 多语言支持
- 默认英文兜底
- 用户可覆盖
- 帮助文档多语言
- 字体回退

### 代价
- 翻译工作量大（主要字符串约 500 个）
- 资源文件维护
- 测试需覆盖多 locale
- RTL 布局调试复杂

### 约束
- 默认 `.resx` 必须是英文
- locale 必须用 BCP 47 标签（`zh-CN` / `ja` / `de`）
- 命令名禁止翻译
- 用户覆盖文件解析失败时降级
- 日期 / 数字格式化必须用 `CultureInfo.CurrentCulture`，禁止硬编码格式
- 字体回退列表必须包含 CJK 字体（避免方块字）
- RTL 必须用 `FlowDirection`，不手动镜像
- 翻译缺失时必须降级到 fallback 链，不显示 key 本身
- 资源文件名必须 `<Base>.<locale>.resx`
- `--locale` 启动参数优先级最高
