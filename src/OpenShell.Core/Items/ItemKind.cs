namespace OpenShell.Items;

/// <summary>
/// Kind of an <see cref="IItem"/>.
/// </summary>
public enum ItemKind
{
    Unknown = 0,
    File,
    Directory,
    SymbolicLink,
    HardLink,
    Junction,
    Container,
    Property,
}
