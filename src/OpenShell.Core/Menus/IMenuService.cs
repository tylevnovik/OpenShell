using OpenShell.Commands;

namespace OpenShell.Menus;

/// <summary>
/// Builds the menu tree from command metadata and answers visibility queries
/// against a <see cref="MenuContext"/>. Per ADR-0028 sections 3 and 5.
/// </summary>
public interface IMenuService
{
    /// <summary>The current menu tree. Rebuilt on every <see cref="Rebuild"/> call.</summary>
    MenuTree Tree { get; }

    /// <summary>
    /// Returns the visible child nodes of a top-level group, filtered and
    /// sorted against the supplied context.
    /// </summary>
    /// <param name="group">Top-level group id (e.g. <c>context</c>, <c>toolbar</c>).</param>
    /// <param name="context">Current menu context used for When evaluation.</param>
    IReadOnlyList<MenuNode> GetVisibleNodes(string group, MenuContext context);

    /// <summary>
    /// Rebuild the menu tree by scanning the supplied command descriptors for
    /// <c>[MenuItem]</c> and <c>[Icon]</c> attributes. Per ADR-0028 section 3.
    /// </summary>
    void Rebuild(IReadOnlyCollection<CommandDescriptor> commands);
}
