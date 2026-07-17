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
/// 输入对话框自定义 Window 实现。Per ADR-0043 §3.
/// 用于「重命名」「新建文件夹」「跳转路径」等单行输入场景。
/// 支持内联校验（Validator 委托），Enter 确认（校验通过），Esc 取消。
/// </summary>
internal sealed class InputDialogWindow : Window
{
    // null 表示用户取消（Esc / Cancel 按钮 / X 按钮关闭）
    private string? _result;

    private readonly TextBox _textBox;
    private readonly TextBlock _errorText;
    private readonly Func<string, string?>? _validator;

    // T-309: i18n 服务（可选; 未注册时为 null, 回退硬编码英文）。
    private readonly II18nService? _i18n;

    // T-309: OK / Cancel 按钮引用, 用于 LocaleChanged 动态刷新。
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    /// <summary>
    /// 构造输入对话框窗口。
    /// </summary>
    /// <param name="options">输入对话框参数（标题 / 标签 / 默认值 / 占位符 / 校验器）。</param>
    public InputDialogWindow(InputDialogOptions options, II18nService? i18n = null)
    {
        // T-309: 从全局 DI 容器解析 II18nService。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        Title = options.Title;
        MinWidth = 400;
        MaxWidth = 600;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _validator = options.Validator;
        _textBox = new TextBox
        {
            Text = options.DefaultValue ?? "",
            Watermark = options.Placeholder ?? "",
            Margin = new Thickness(0, 4, 0, 4),
        };
        _errorText = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            Margin = new Thickness(0, 2, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            // 初始无错误，隐藏占位
            IsVisible = false,
        };

        var contentChildren = new List<Control>();

        // 标签（可选）
        if (!string.IsNullOrEmpty(options.Label))
        {
            contentChildren.Add(new TextBlock
            {
                Text = options.Label,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        contentChildren.Add(_textBox);
        contentChildren.Add(_errorText);

        // 按钮行：OK（默认） + Cancel
        _okButton = new Button
        {
            Content = T("common.ok"),
            Margin = new Thickness(4, 0),
            Padding = new Thickness(16, 4),
            MinWidth = 80,
            IsDefault = true,
        };
        _cancelButton = new Button
        {
            Content = T("common.cancel"),
            Margin = new Thickness(4, 0),
            Padding = new Thickness(16, 4),
            MinWidth = 80,
        };

        _okButton.Click += (_, _) => TryAccept();
        _cancelButton.Click += (_, _) =>
        {
            _result = null;
            Close();
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _okButton, _cancelButton },
        };
        contentChildren.Add(buttonPanel);

        var contentPanel = new StackPanel();
        contentPanel.Children.AddRange(contentChildren);
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = contentPanel,
        };

        // T-309: 订阅 LocaleChanged 事件，动态切换语言后刷新按钮标签。
        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
        }
    }

    /// <summary>
    /// 显示模态对话框并返回用户输入。
    /// 使用非泛型 ShowDialog + 字段读取，确保 X 按钮关闭也返回 null.
    /// </summary>
    public async Task<string?> ShowDialogAsync(Window owner)
    {
        await this.ShowDialog(owner);
        return _result;
    }

    /// <summary>
    /// 窗口打开后聚焦 TextBox 并全选，方便用户直接覆盖输入。
    /// </summary>
    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        _textBox.Focus();
        _textBox.SelectAll();
    }

    /// <summary>
    /// 键盘处理：Enter 确认（校验通过则关闭），Esc 取消。
    /// Per ADR-0043 §11 a11y 约束。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result = null;
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            TryAccept();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// 尝试接受输入：调用 Validator，通过则关闭返回输入值；失败则显示错误。
    /// </summary>
    private void TryAccept()
    {
        var text = _textBox.Text ?? "";
        if (_validator is { } validator && validator(text) is { } error)
        {
            // 校验失败：显示错误，保持窗口打开
            _errorText.Text = error;
            _errorText.IsVisible = true;
            return;
        }

        _result = text;
        Close();
    }

    /// <summary>T-309: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <summary>T-309: LocaleChanged 事件处理：在 UI 线程刷新按钮标签翻译。</summary>
    private void OnLocaleChanged(object? sender, string e)
    {
        Dispatcher.UIThread.Post(ApplyTranslations);
    }

    /// <summary>T-309: 集中刷新 OK / Cancel 按钮标签翻译。LocaleChanged 事件触发。</summary>
    private void ApplyTranslations()
    {
        _okButton.Content = T("common.ok");
        _cancelButton.Content = T("common.cancel");
    }
}
