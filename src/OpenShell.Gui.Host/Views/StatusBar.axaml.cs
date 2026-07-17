using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenShell.Gui.Host.Views;

public partial class StatusBar : UserControl
{
    public StatusBar()
    {
        InitializeComponent();
        var border = this.FindControl<Border>("PART_Border");
        if (border != null)
        {
            border.Background = Brushes.LightGray;
        }
    }
}
