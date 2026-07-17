using System.Collections.ObjectModel;
using OpenShell.Paths;

namespace OpenShell.Gui.Host.ViewModels;

public sealed class NavigationItem
{
    public string Label { get; init; } = "";
    public string? IconGlyph { get; init; }
    public ItemPath? Path { get; init; }
    public ObservableCollection<NavigationItem> Children { get; } = new();
}
