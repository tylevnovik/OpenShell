using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.I18n;
using OpenShell.Items;

namespace OpenShell.Gui.Host.Views;

/// <summary>
/// 全局搜索窗口占位。Per ADR-0030 §6.
/// Ctrl+Shift+F 触发, 弹出居中模态窗口: 搜索框 + 结果列表 + 状态栏。
/// 双击结果调 <see cref="GlobalSearchViewModel.NavigateToResultCommand"/> (per ADR-0030 §6 双击跳转)。
/// Esc 关闭窗口 (per ADR-0030 §4 / §6)。
/// 不使用 axaml, 纯 C# code-behind (与 MainWindow 风格一致)。
/// </summary>
internal sealed class GlobalSearchWindow : Window
{
    private readonly TextBox _queryBox = new()
    {
        Watermark = "Search files (Ctrl+Shift+F)...",
        Margin = new Thickness(4),
    };
    private readonly CheckBox _includeContentsBox = new() { Margin = new Thickness(4, 4, 8, 4) };
    private readonly Button _cancelButton = new() { Margin = new Thickness(4), IsVisible = false };
    private readonly ListBox _resultsList = new() { Margin = new Thickness(4) };
    private readonly TextBlock _indexStatusText = new() { Margin = new Thickness(4, 0, 4, 0) };
    private readonly TextBlock _statusText = new() { Margin = new Thickness(4, 0, 4, 4) };

    private GlobalSearchViewModel? _vm;
    private readonly II18nService? _i18n;

    public GlobalSearchWindow(II18nService? i18n = null)
    {
        // T-312: 从全局 DI 容器解析 II18nService (可选; 未注册时为 null, 回退硬编码英文)。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        Title = T("gui.search.title");
        Width = 700;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // T-312: 应用初始翻译 (覆盖字段初始化的英文占位符)。
        ApplyTranslations();
        _includeContentsBox.Content = T("gui.search.includeContents");
        _cancelButton.Content = T("gui.search.cancel");

        // T-312: 订阅 LocaleChanged 事件，动态切换语言后刷新窗口标题 + watermark。
        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
        }

        // 结果项模板: 显示 Name + Path。
        _resultsList.ItemTemplate = new FuncDataTemplate<IItem>((_, _) =>
        {
            var name = new TextBlock { FontWeight = FontWeight.Bold };
            name.Bind(TextBlock.TextProperty, new Binding("Name"));
            var path = new TextBlock { FontSize = 11, Foreground = Brushes.Gray };
            path.Bind(TextBlock.TextProperty, new Binding("Path.Display"));
            return new StackPanel { Margin = new Thickness(2), Children = { name, path } };
        });

        // Avalonia 11: 附加属性 DockPanel.Dock 不能在对象初始化器中赋值, 需通过 SetDock 设置。Per MainWindow.cs 模式。
        var queryBorder = new Border
        {
            Padding = new Thickness(4),
            Child = new DockPanel
            {
                Children =
                {
                    DockControl(_cancelButton, Dock.Right),
                    DockControl(_includeContentsBox, Dock.Right),
                    _queryBox,
                },
            },
        };
        DockPanel.SetDock(queryBorder, Dock.Top);

        var statusBorder = new Border
        {
            Background = Brushes.LightGray,
            Padding = new Thickness(8, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _indexStatusText, _statusText },
            },
        };
        DockPanel.SetDock(statusBorder, Dock.Bottom);

        Content = new DockPanel
        {
            Children =
            {
                queryBorder,
                statusBorder,
                _resultsList,
            },
        };

        // Esc 关闭 (per ADR-0030 §4 搜索框)。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        // T-312: 关闭窗口时解除 LocaleChanged 订阅避免泄漏。
        Closed += (_, _) =>
        {
            if (_i18n is not null)
            {
                _i18n.LocaleChanged -= OnLocaleChanged;
            }
            _vm?.Dispose();
            _vm = null;
        };

        // 双击结果 → NavigateToResultCommand + 关闭窗口。
        _resultsList.DoubleTapped += (_, _) =>
        {
            if (_vm is null) return;
            if (_resultsList.SelectedItem is IItem item)
            {
                _ = _vm.NavigateToResultCommand.Execute(item);
            }
            Close();
        };

        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as GlobalSearchViewModel;
            if (_vm is null) return;

            _queryBox.Bind(TextBox.TextProperty, new Binding("Query"));
            _includeContentsBox.Bind(ToggleButton.IsCheckedProperty, new Binding("IncludeContents")
            {
                Mode = BindingMode.TwoWay,
            });
            _cancelButton.Bind(Button.CommandProperty, new Binding("CancelCommand"));
            _cancelButton.Bind(Visual.IsVisibleProperty, new Binding("IsSearching"));
            _resultsList.ItemsSource = _vm.Results;
            _indexStatusText.Bind(TextBlock.TextProperty, new Binding("IndexStatusText"));
            _statusText.Bind(TextBlock.TextProperty, new Binding("StatusText"));

            // Enter 触发搜索 (即时, 不等防抖)。
            _queryBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) _ = _vm.SearchCommand.Execute();
            };

            _queryBox.Focus();
        };

        _includeContentsBox.IsCheckedChanged += (_, _) => TriggerImmediateSearch();
    }

    private static T DockControl<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private void TriggerImmediateSearch()
    {
        if (_vm is not null && !string.IsNullOrWhiteSpace(_vm.Query))
            _vm.SearchCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>T-312: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key switch
    {
        "gui.search.includeContents" => "Search contents",
        "gui.search.cancel" => "Cancel",
        _ => key,
    };

    /// <summary>T-312: LocaleChanged 事件处理：在 UI 线程刷新窗口标题 + watermark。</summary>
    private void OnLocaleChanged(object? sender, string e)
    {
        Dispatcher.UIThread.Post(ApplyTranslations);
    }

    /// <summary>T-312: 集中刷新窗口标题 + 搜索框 watermark 的翻译。构造函数末尾 + LocaleChanged 事件触发。</summary>
    private void ApplyTranslations()
    {
        Title = T("gui.search.title");
        _queryBox.Watermark = T("gui.search.watermark.full");
        _includeContentsBox.Content = T("gui.search.includeContents");
        _cancelButton.Content = T("gui.search.cancel");
    }
}
