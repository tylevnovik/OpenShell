namespace OpenShell.Menus;

/// <summary>
/// Builds a hierarchical menu tree from flat <see cref="MenuItemContribution"/>
/// records by splitting each <see cref="MenuItemContribution.Path"/> on '/'.
/// Per ADR-0028 section 3.
/// </summary>
public sealed class MenuTree
{
    /// <summary>Root node. Its children are top-level groups (context, toolbar, etc.).</summary>
    public MenuNode Root { get; } = new("");

    /// <summary>
    /// Inserts a contribution into the tree. Intermediate nodes are created as needed.
    /// The leaf node receives the contribution; separator contributions mark the node.
    /// Adding the same path twice overwrites the previous contribution.
    /// </summary>
    public void Add(MenuItemContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);

        var segments = contribution.Path.Split('/');
        var node = Root;
        foreach (var seg in segments)
        {
            if (string.IsNullOrEmpty(seg)) continue;
            node = node.FindChild(seg) ?? node.AddChild(seg);
        }

        node.Contribution = contribution;
        node.IsSeparator = contribution.IsSeparator;
        node.Order = contribution.Order;
    }

    /// <summary>
    /// Returns the immediate children of a top-level group (e.g.
    /// <c>context</c>, <c>toolbar</c>). Returns an empty list if the group
    /// does not exist.
    /// </summary>
    public IReadOnlyList<MenuNode> GetGroup(string group)
    {
        var groupNode = Root.FindChild(group);
        return groupNode?.Children ?? new List<MenuNode>();
    }
}
