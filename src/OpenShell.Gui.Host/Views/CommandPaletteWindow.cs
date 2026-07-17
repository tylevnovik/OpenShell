using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.I18n;

namespace OpenShell.Gui.Host.Views;

/// <summary>
/// T-443: 命令面板窗口。Per ADR-0013 §6 / ADR-0023.
/// Ctrl+Shift+P 触发，居中模态窗口：搜索框 + 命令列表 + 状态栏。
/// Enter 执行选中命令，Esc 关闭，上下键导航。
/// 纯 C# code-behind，与 MainWindow/GlobalSearchWindow 风格一致。
/// </summary>
internal sealed class CommandPaletteWindow : Window
{
    private readonly TextBox _queryBox = new()
    {
        Watermark = "gui.commandPalette.watermark",
        Margin = new Thickness(4),
    };
    private readonly ListBox _resultsList = new() { Margin = new Thickness(4) };
    private readonly TextBlock _statusText = new() { Margin = new Thickness(4, 0, 4, 4) };

    private CommandPaletteViewModel? _vm;
    private readonly II18nService? _i18n;

    // T-448: 构造函数注入 II18nService，替代 Service Locator
    public CommandPaletteWindow(II18nService? i18n = null)
    {
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        Title = T("gui.commandPalette.title");
        Width = 600;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ApplyTranslations();

        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
        }

        // 结果项模板: 显示命令名 + 描述
        _resultsList.ItemTemplate = new FuncDataTemplate<CommandPaletteItem>((_, _) =>
        {
            var name = new TextBlock { FontWeight = FontWeight.Bold };
            name.Bind(TextBlock.TextProperty, new Binding("DisplayText"));
            var desc = new TextBlock { FontSize = 11, Foreground = Brushes.Gray };
            desc.Bind(TextBlock.TextProperty, new Binding("Description"));
            return new StackPanel { Margin = new Thickness(2), Spacing = 2, Children = { name, desc } };
        });

        var queryBorder = new Border
        {
            Padding = new Thickness(4),
            Child = _queryBox,
        };
        DockPanel.SetDock(queryBorder, Dock.Top);

        var statusBorder = new Border
        {
            Background = Brushes.LightGray,
            Padding = new Thickness(8, 2),
            Child = _statusText,
        };
        DockPanel.SetDock(statusBorder, Dock.Bottom);

        Content = new DockPanel
        {
            Children = { queryBorder, statusBorder, _resultsList },
        };

        // Esc 关闭
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Closed += (_, _) =>
        {
            if (_i18n is not null)
            {
                _i18n.LocaleChanged -= OnLocaleChanged;
            }
        };

        // 双击/Enter 执行选中命令
        _resultsList.DoubleTapped += (_, _) =>
        {
            if (_vm is not null && _resultsList.SelectedItem is CommandPaletteItem item)
            {
                _ = _vm.ExecuteCommand.Execute(item);
            }
            Close();
        };

        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as CommandPaletteViewModel;
            if (_vm is null) return;

            _queryBox.Bind(TextBox.TextProperty, new Binding("Query") { Mode = BindingMode.TwoWay });
            _resultsList.ItemsSource = _vm.Items;
            _statusText.Bind(TextBlock.TextProperty, new Binding("StatusText"));
            _resultsList.Bind(ListBox.SelectedItemProperty, new Binding("SelectedItem") { Mode = BindingMode.TwoWay, Source = _vm });

            // Enter 执行选中命令
            _queryBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _vm.SelectedItem is not null)
                {
                    _ = _vm.ExecuteCommand.Execute(_vm.SelectedItem);
                    Close();
                }
            };

            _queryBox.Focus();
        };
    }

    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    private void OnLocaleChanged(object? sender, string e)
    {
        Dispatcher.UIThread.Post(ApplyTranslations);
    }

    private void ApplyTranslations()
    {
        Title = T("gui.commandPalette.title");
        _queryBox.Watermark = T("gui.commandPalette.watermark");
    }
}
