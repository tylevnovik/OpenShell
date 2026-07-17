using System.Reflection;
using OpenShell.Commands;
using OpenShell.When;

namespace OpenShell.Menus;

/// <summary>
/// Default <see cref="IMenuService"/>. Scans command types for
/// <c>[MenuItem]</c> and <c>[Icon]</c> attributes, builds a <see cref="MenuTree"/>,
/// and answers visibility queries by evaluating When expressions against a
/// <see cref="MenuContext"/>. Per ADR-0028 sections 1, 3, 9, 10.
/// </summary>
public sealed class MenuService : IMenuService
{
    /// <inheritdoc />
    public MenuTree Tree { get; } = new();

    /// <summary>Initializes a new instance, optionally building the tree immediately.</summary>
    /// <param name="commands">
    /// Optional initial command set. If non-null, <see cref="Rebuild(IReadOnlyCollection{CommandDescriptor})"/>
    /// is invoked immediately.
    /// </param>
    public MenuService(IReadOnlyCollection<CommandDescriptor>? commands = null)
    {
        if (commands is not null)
        {
            Rebuild(commands);
        }
    }

    /// <inheritdoc />
    public void Rebuild(IReadOnlyCollection<CommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        RebuildInternal(commands.Select(d => d.CommandType));
    }

    /// <summary>
    /// Rebuild the menu tree by scanning the supplied command types directly.
    /// Convenience overload used by tests and by callers that do not have
    /// fully constructed <see cref="CommandDescriptor"/> instances. The
    /// command id (full name) is derived from the <c>[Verb]</c> attribute.
    /// </summary>
    public void Rebuild(IEnumerable<Type> commandTypes)
    {
        ArgumentNullException.ThrowIfNull(commandTypes);
        RebuildInternal(commandTypes);
    }

    private void RebuildInternal(IEnumerable<Type> commandTypes)
    {
        // Reset by replacing the underlying tree. The public Tree property
        // exposes a single instance, so we clear it instead of swapping.
        Tree.Root.Children.Clear();

        foreach (var type in commandTypes)
        {
            if (type is null) continue;
            if (!type.IsClass || type.IsAbstract) continue;

            var menuAttrs = type.GetCustomAttributes<MenuItemAttribute>();
            if (!menuAttrs.Any()) continue;

            var iconPath = type.GetCustomAttribute<IconAttribute>()?.Path;
            var commandId = GetCommandId(type);

            foreach (var attr in menuAttrs)
            {
                var label = attr.Label ?? DeriveLabel(attr.Path);
                var contribution = new MenuItemContribution
                {
                    Path = attr.Path,
                    CommandId = commandId,
                    Label = label,
                    LabelKey = attr.LabelKey,
                    When = attr.When,
                    Order = attr.Order,
                    IsSeparator = attr.IsSeparator,
                    IsDynamic = attr.IsDynamic,
                    IconPath = iconPath,
                };
                Tree.Add(contribution);
            }
        }

        SortTree(Tree.Root);
    }

    /// <inheritdoc />
    public IReadOnlyList<MenuNode> GetVisibleNodes(string group, MenuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var groupNodes = Tree.GetGroup(group);
        var ctx = context.ToDictionary();
        var visible = new List<MenuNode>();

        foreach (var node in groupNodes)
        {
            if (!IsVisible(node, ctx)) continue;
            visible.Add(node);
        }

        // Already sorted at build time, but re-sort defensively in case the
        // tree was mutated externally. Sort by Order then by Label (or Id).
        visible.Sort(CompareNodes);
        return visible;
    }

    private static bool IsVisible(MenuNode node, IReadOnlyDictionary<string, object?> context)
    {
        // Per ADR-0028: When expression failure (parse or evaluation) means
        // the item is not shown without raising an error.
        var when = node.Contribution?.When;
        if (string.IsNullOrWhiteSpace(when)) return true;

        try
        {
            return WhenExpression.Parse(when).Evaluate(context);
        }
        catch (WhenParseException)
        {
            return false;
        }
    }

    private static void SortTree(MenuNode node)
    {
        node.Children.Sort(CompareNodes);
        foreach (var child in node.Children)
        {
            SortTree(child);
        }
    }

    private static int CompareNodes(MenuNode a, MenuNode b)
    {
        var byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0) return byOrder;

        var labelA = a.Contribution?.Label ?? a.Id;
        var labelB = b.Contribution?.Label ?? b.Id;
        return string.Compare(labelA, labelB, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCommandId(Type commandType)
    {
        var verbAttr = commandType.GetCustomAttribute<VerbAttribute>();
        if (verbAttr is null) return commandType.FullName ?? commandType.Name;

        return string.IsNullOrEmpty(verbAttr.Noun)
            ? verbAttr.Verb.ToLowerInvariant()
            : $"{verbAttr.Verb.ToLowerInvariant()}-{verbAttr.Noun.ToLowerInvariant()}";
    }

    private static string DeriveLabel(string path)
    {
        var segments = path.Split('/');
        var last = segments.Length > 0 ? segments[^1] : path;
        if (string.IsNullOrEmpty(last)) return path;

        if (last.Length == 1)
        {
            return last.ToUpperInvariant();
        }

        return char.ToUpperInvariant(last[0]) + last[1..];
    }
}
