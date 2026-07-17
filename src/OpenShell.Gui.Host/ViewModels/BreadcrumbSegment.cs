using OpenShell.Paths;

namespace OpenShell.Gui.Host.ViewModels;

public sealed class BreadcrumbSegment
{
    public string Label { get; init; } = "";
    public ItemPath Path { get; init; }
}
