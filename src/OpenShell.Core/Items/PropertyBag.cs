using System.Collections.Immutable;

namespace OpenShell.Items;

/// <summary>
/// Immutable bag of provider-specific properties.
/// Per ADR-0003: backed by <see cref="ImmutableDictionary{TKey,TValue}"/>, never exposes mutable state.
/// </summary>
public readonly record struct PropertyBag
{
    public static PropertyBag Empty { get; } = new(ImmutableDictionary<string, object?>.Empty);

    private readonly ImmutableDictionary<string, object?> _values;
    public ImmutableDictionary<string, object?> Values => _values ?? ImmutableDictionary<string, object?>.Empty;

    public PropertyBag(ImmutableDictionary<string, object?> values) => _values = values;

    public object? this[string key] => Values.TryGetValue(key, out var v) ? v : null;

    public PropertyBag With(string key, object? value) => new(Values.SetItem(key, value));
    public PropertyBag Without(string key) => new(Values.Remove(key));
}
