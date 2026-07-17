using System.Collections.Concurrent;
using OpenShell.Paths;

namespace OpenShell.Locations;

/// <summary>
/// Default <see cref="ILocationStack"/> implementation backed by a
/// <see cref="ConcurrentStack{T}"/>. Thread-safe for concurrent push/pop.
/// Per ADR-0048 §3.6.
/// </summary>
public sealed class LocationStack : ILocationStack
{
    private readonly ConcurrentStack<ItemPath> _stack = new();

    /// <inheritdoc />
    public void Push(ItemPath location)
    {
        _stack.Push(location);
    }

    /// <inheritdoc />
    public ItemPath Pop()
    {
        if (!_stack.TryPop(out var location))
            throw new InvalidOperationException("The location stack is empty.");
        return location;
    }

    /// <inheritdoc />
    public bool TryPop(out ItemPath location) => _stack.TryPop(out location);

    /// <inheritdoc />
    public int Count => _stack.Count;
}
