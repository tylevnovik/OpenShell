namespace OpenShell.Menus;

/// <summary>
/// Generates menu children at runtime. Per ADR-0028 section 10.
/// Implemented by command types decorated with
/// <c>[MenuItem(IsDynamic = true)]</c>.
/// </summary>
public interface IDynamicMenuProvider
{
    /// <summary>
    /// Generate child menu nodes for the given context.
    /// </summary>
    /// <param name="context">Current menu context.</param>
    /// <returns>List of generated child nodes.</returns>
    IReadOnlyList<MenuNode> Generate(MenuContext context);
}
