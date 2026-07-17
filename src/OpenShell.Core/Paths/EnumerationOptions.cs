namespace OpenShell.Paths;

/// <summary>
/// Options controlling item enumeration in <see cref="OpenShell.Providers.IContainerProvider.GetChildrenAsync"/>.
/// </summary>
public sealed record EnumerationOptions
{
    public bool Recurse { get; init; }
    public string? Filter { get; init; }
    public bool IncludeHidden { get; init; } = true;
    public bool IncludeSystem { get; init; } = true;
    public int MaxDepth { get; init; } = -1;
    public StringComparison FilterComparison { get; init; } = StringComparison.OrdinalIgnoreCase;
}
