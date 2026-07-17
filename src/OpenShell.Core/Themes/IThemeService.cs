namespace OpenShell.Themes;

/// <summary>
/// Theme service abstraction. Per ADR-0027 section 1.
/// Manages the active theme and exposes the list of available themes
/// (built-in plus user-defined from the themes directory).
/// Consumers subscribe to <see cref="Changed"/> to react to theme switches.
/// </summary>
public interface IThemeService
{
    /// <summary>Currently active theme.</summary>
    Theme Current { get; }

    /// <summary>All available themes: built-ins first, then user themes from the themes directory.</summary>
    IReadOnlyList<Theme> Available { get; }

    /// <summary>Apply a theme instance as the active theme and notify subscribers.</summary>
    /// <param name="theme">Theme to apply. Must not be null.</param>
    void Apply(Theme theme);

    /// <summary>
    /// Apply a theme by name (case-insensitive) from the available themes.
    /// </summary>
    /// <param name="name">Theme name to look up.</param>
    /// <exception cref="ArgumentException">Thrown if no theme with the given name exists.</exception>
    void Apply(string name);

    /// <summary>Observable stream that emits the new theme whenever the active theme changes.</summary>
    IObservable<Theme> Changed { get; }
}
