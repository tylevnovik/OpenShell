using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Abstractions;
using OpenShell.I18n;

namespace OpenShell.Gui.Host.Services;

/// <summary>
/// 消息框自定义 Window 实现。Per ADR-0043 §3.
/// 项目未引用 Avalonia.Dialogs 包（无 ContentDialog），故用自定义 Window.
/// 支持 Esc 关闭返回 Cancel，Enter 触发默认按钮，Detail 折叠区域。
/// </summary>
internal sealed class MessageBoxWindow : Window
{
    // 默认结果 Cancel，覆盖 X 按钮关闭 / Esc 关闭场景。Per ADR-0043 §11「Esc 永远返回 Cancel」。
    private DialogResult _result = DialogResult.Cancel;

    // Enter 键触发的默认按钮结果（由 MapButtons 计算）
    private readonly DialogResult _defaultResult;

    // T-308: i18n 服务（可选; 未注册时为 null, 回退硬编码英文）。
    private readonly II18nService? _i18n;

    // T-308: 按钮列表 + 详情折叠区引用, 用于 LocaleChanged 动态刷新。
    private readonly List<(Button Button, DialogResult Result)> _buttons = new();
    private Expander? _detailExpander;

    /// <summary>
    /// 构造消息框窗口。
    /// </summary>
    /// <param name="options">消息框参数（标题 / 消息 / 类型 / 按钮 / Detail / RelatedPath）。</param>
    public MessageBoxWindow(MessageBoxOptions options, II18nService? i18n = null)
    {
        // T-308: 从全局 DI 容器解析 II18nService。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        Title = options.Title;
        MinWidth = 360;
        MaxWidth = 560;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var (defaultResult, buttons) = MapButtons(options.Buttons);
        _defaultResult = defaultResult;

        var contentChildren = new List<Control>();

        // 顶部：图标 + 消息正文（横向布局）
        var iconText = new TextBlock
        {
            Text = GetIconGlyph(options.Kind),
            FontSize = 28,
            Foreground = GetIconBrush(options.Kind),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var messageText = new TextBlock
        {
            Text = options.Message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { iconText, messageText },
        };
        contentChildren.Add(headerPanel);

        // Detail 折叠区（堆栈 / 上下文），可选。参考 VS Code 错误对话框。
        if (!string.IsNullOrEmpty(options.Detail))
        {
            var detailText = new SelectableTextBlock
            {
                Text = options.Detail,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = FontFamily.Parse("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 12,
            };
            var expander = new Expander
            {
                Header = T("common.details"),
                Content = detailText,
                Margin = new Thickness(0, 12, 0, 0),
            };
            _detailExpander = expander;
            contentChildren.Add(expander);
        }

        // RelatedPath 显示（用于「在文件管理器中显示」），灰色小字
        if (options.RelatedPath is { } related)
        {
            var pathText = new TextBlock
            {
                Text = related.Display,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            contentChildren.Add(pathText);
        }

        // 按钮行：根据 MessageBoxButtons 映射
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        foreach (var (label, result) in buttons)
        {
            var button = new Button
            {
                Content = label,
                Margin = new Thickness(4, 0),
                Padding = new Thickness(16, 4),
                MinWidth = 80,
                IsDefault = result == defaultResult,
            };
            _buttons.Add((button, result));
            // 闭包捕获 result，点击后设置 _result 并关闭
            var captured = result;
            button.Click += (_, _) =>
            {
                _result = captured;
                Close();
            };
            buttonPanel.Children.Add(button);
        }

        contentChildren.Add(buttonPanel);

        var contentPanel = new StackPanel();
        contentPanel.Children.AddRange(contentChildren);
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = contentPanel,
        };

        // T-308: 订阅 LocaleChanged 事件，动态切换语言后刷新按钮 / 详情标题。
        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
        }
    }

    /// <summary>
    /// 显示模态对话框并返回结果。
    /// 使用非泛型 ShowDialog + 字段读取，确保 X 按钮关闭也返回 Cancel.
    /// </summary>
    public async Task<DialogResult> ShowDialogAsync(Window owner)
    {
        await this.ShowDialog(owner);
        return _result;
    }

    /// <summary>
    /// 键盘处理：Esc 关闭返回 Cancel，Enter 触发默认按钮。
    /// Per ADR-0043 §11 a11y 约束。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result = DialogResult.Cancel;
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _result = _defaultResult;
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// 映射 MessageBoxButtons → 按钮列表 + 默认按钮结果。
    /// Windows 习惯：主按钮（OK/Yes）在左，Cancel 在右。
    /// </summary>
    private (DialogResult Default, IReadOnlyList<(string Label, DialogResult Result)> Buttons) MapButtons(
        MessageBoxButtons buttons)
    {
        return buttons switch
        {
            MessageBoxButtons.OK => (
                DialogResult.OK,
                new[] { (T("common.ok"), DialogResult.OK) }),
            MessageBoxButtons.OKCancel => (
                DialogResult.OK,
                new[] { (T("common.ok"), DialogResult.OK), (T("common.cancel"), DialogResult.Cancel) }),
            MessageBoxButtons.YesNo => (
                DialogResult.Yes,
                new[] { (T("common.yes"), DialogResult.Yes), (T("common.no"), DialogResult.No) }),
            MessageBoxButtons.YesNoCancel => (
                DialogResult.Yes,
                new[] { (T("common.yes"), DialogResult.Yes), (T("common.no"), DialogResult.No), (T("common.cancel"), DialogResult.Cancel) }),
            _ => (
                DialogResult.OK,
                new[] { (T("common.ok"), DialogResult.OK) }),
        };
    }

    /// <summary>
    /// 根据 MessageBoxKind 返回图标字形（Unicode 符号，跨平台兼容）。
    /// </summary>
    private static string GetIconGlyph(MessageBoxKind kind) => kind switch
    {
        MessageBoxKind.Information => "ℹ",   // U+2139 INFORMATION SOURCE
        MessageBoxKind.Warning => "⚠",       // U+26A0 WARNING SIGN
        MessageBoxKind.Error => "✖",         // U+2716 HEAVY MULTIPLICATION X
        MessageBoxKind.Question => "?",      // 简单问号，避免 emoji 渲染差异
        _ => "ℹ",
    };

    /// <summary>
    /// 根据 MessageBoxKind 返回图标颜色（与 FluentTheme 风格一致）。
    /// </summary>
    private static IBrush GetIconBrush(MessageBoxKind kind) => kind switch
    {
        MessageBoxKind.Information => Brushes.DodgerBlue,
        MessageBoxKind.Warning => Brushes.Orange,
        MessageBoxKind.Error => Brushes.IndianRed,
        MessageBoxKind.Question => Brushes.DodgerBlue,
        _ => Brushes.DodgerBlue,
    };

    /// <summary>T-308: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <summary>T-308: LocaleChanged 事件处理：在 UI 线程刷新按钮 / 详情标题翻译。</summary>
    private void OnLocaleChanged(object? sender, string e)
    {
        Dispatcher.UIThread.Post(ApplyTranslations);
    }

    /// <summary>
    /// T-308: 集中刷新按钮标签 / 详情标题翻译。LocaleChanged 事件触发。
    /// </summary>
    private void ApplyTranslations()
    {
        foreach (var (button, result) in _buttons)
        {
            button.Content = T(ResultToKey(result));
        }

        if (_detailExpander is { } expander)
        {
            expander.Header = T("common.details");
        }
    }

    /// <summary>T-308: DialogResult → 翻译 key 映射, 用于动态刷新按钮标签。</summary>
    private static string ResultToKey(DialogResult result) => result switch
    {
        DialogResult.OK => "common.ok",
        DialogResult.Cancel => "common.cancel",
        DialogResult.Yes => "common.yes",
        DialogResult.No => "common.no",
        _ => "common.ok",
    };
}
