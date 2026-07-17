namespace OpenShell.Items;

/// <summary>
/// Immutable timestamps associated with an <see cref="IItem"/>.
/// </summary>
public readonly record struct ItemTimestamps(
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    DateTimeOffset? Accessed)
{
    public static ItemTimestamps None => default;
}
