using OpenShell.Paths;

namespace OpenShell.Items;

/// <summary>
/// Default immutable <see cref="IItem"/> implementation. Per ADR-0003, sealed record with init-only properties.
/// </summary>
public sealed record Item : IItem
{
    public required ItemPath Path { get; init; }
    public required ItemKind Kind { get; init; }
    public ItemTimestamps Timestamps { get; init; } = ItemTimestamps.None;
    public long? Size { get; init; }
    public string? ContentType { get; init; }
    public PropertyBag Properties { get; init; } = PropertyBag.Empty;

    public string Name => Path.GetName();

    /// <summary>Convenience factory for a file.</summary>
    public static Item File(ItemPath path, long? size = null, DateTimeOffset? modified = null) => new()
    {
        Path = path,
        Kind = ItemKind.File,
        Size = size,
        Timestamps = new ItemTimestamps(null, modified, null),
    };

    /// <summary>Convenience factory for a directory.</summary>
    public static Item Directory(ItemPath path) => new()
    {
        Path = path,
        Kind = ItemKind.Directory,
    };
}
