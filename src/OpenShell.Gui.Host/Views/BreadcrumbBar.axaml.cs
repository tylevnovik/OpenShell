using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OpenShell.Gui.Host.ViewModels;

namespace OpenShell.Gui.Host.Views;

public partial class BreadcrumbBar : UserControl
{
    public BreadcrumbBar()
    {
        InitializeComponent();
    }

    private async void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.CommitAddressBarAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelAddressBarEdit();
        }
    }
}
