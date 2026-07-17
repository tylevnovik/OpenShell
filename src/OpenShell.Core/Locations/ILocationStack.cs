using OpenShell.Paths;

namespace OpenShell.Locations;

/// <summary>
/// Stack of <see cref="ItemPath"/> locations for <c>Push-Location</c> / <c>Pop-Location</c>.
/// Per ADR-0048 §3.6. Registered as a singleton in the host DI container so that
/// each host (CLI / GUI) maintains an isolated stack and tests can substitute a fresh
/// instance per fixture.
/// </summary>
public interface ILocationStack
{
    /// <summary>Push the current location onto the stack.</summary>
    /// <param name="location">Location to push.</param>
    void Push(ItemPath location);

    /// <summary>Pop the most recently pushed location. Throws <see cref="InvalidOperationException"/> when empty.</summary>
    /// <returns>The popped location.</returns>
    ItemPath Pop();

    /// <summary>Try to pop the most recently pushed location. Returns <c>false</c> when empty (no throw).</summary>
    /// <param name="location">When this method returns <c>true</c>, contains the popped location.</param>
    /// <returns><c>true</c> if a location was popped; <c>false</c> if the stack was empty.</returns>
    bool TryPop(out ItemPath location);

    /// <summary>Number of locations currently on the stack.</summary>
    int Count { get; }
}
