using OpenShell.Paths;

namespace OpenShell.Items;

/// <summary>
/// Immutable item contract. Per ADR-0003, implementations must be records with no setters.
/// </summary>
public interface IItem
{
    ItemPath Path { get; }
    ItemKind Kind { get; }
    ItemTimestamps Timestamps { get; }
    long? Size { get; }
    string? ContentType { get; }
    PropertyBag Properties { get; }
    string Name { get; }
}
