namespace OpenShell.Menus;

/// <summary>
/// A node in the menu tree. Per ADR-0028 section 3.
/// Intermediate nodes group contributions; leaf nodes carry the
/// <see cref="Contribution"/>. Separators are represented as nodes whose
/// <see cref="IsSeparator"/> is true.
/// </summary>
public sealed class MenuNode
{
    /// <summary>Segment id, e.g. <c>copy</c> or <c>context</c>.</summary>
    public string Id { get; }

    /// <summary>Parent node, or null for the root.</summary>
    public MenuNode? Parent { get; set; }

    /// <summary>Child nodes, ordered by <see cref="Order"/> then by <see cref="Id"/>.</summary>
    public List<MenuNode> Children { get; } = new();

    /// <summary>Contribution attached to a leaf node, or null for intermediate/group nodes.</summary>
    public MenuItemContribution? Contribution { get; set; }

    /// <summary>True if this node represents a separator (drawn as a horizontal rule).</summary>
    public bool IsSeparator { get; set; }

    /// <summary>Sort order within siblings. Lower values come first.</summary>
    public int Order { get; set; }

    /// <summary>Initializes a new node with the given id.</summary>
    /// <param name="id">Segment id.</param>
    public MenuNode(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    /// <summary>Creates and attaches a child node with the given id.</summary>
    public MenuNode AddChild(string id)
    {
        var child = new MenuNode(id) { Parent = this };
        Children.Add(child);
        return child;
    }

    /// <summary>Returns the first child with the given id, or null.</summary>
    public MenuNode? FindChild(string id) =>
        Children.FirstOrDefault(c => c.Id == id);

    /// <summary>True if this node has no children.</summary>
    public bool IsLeaf => Children.Count == 0;
}
