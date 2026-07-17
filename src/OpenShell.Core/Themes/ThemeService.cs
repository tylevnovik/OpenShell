using System.Reactive.Subjects;

namespace OpenShell.Themes;

/// <summary>
/// Default <see cref="IThemeService"/> implementation. Per ADR-0027 section 1.
/// Combines built-in themes with user-defined themes loaded from the themes directory.
/// The active theme defaults to the built-in Dark theme and can be changed via the Apply methods.
/// Theme changes are broadcast through the <see cref="Changed"/> observable.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly TomlThemeLoader _loader;
    private readonly Subject<Theme> _changed = new();
    private readonly IReadOnlyList<Theme> _available;
    private Theme _current;

    /// <summary>
    /// Construct a ThemeService.
    /// </summary>
    /// <param name="loader">
    /// Optional TOML theme loader for user themes. If null, a loader pointing at the default
    /// themes directory is created.
    /// </param>
    public ThemeService(TomlThemeLoader? loader = null)
    {
        _loader = loader ?? new TomlThemeLoader();
        _available = BuildAvailableThemes();
        _current = BuiltInThemes.Dark;
    }

    /// <inheritdoc />
    public Theme Current => _current;

    /// <inheritdoc />
    public IReadOnlyList<Theme> Available => _available;

    /// <inheritdoc />
    public IObservable<Theme> Changed => _changed;

    /// <inheritdoc />
    public void Apply(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _current = theme;
        _changed.OnNext(theme);
    }

    /// <inheritdoc />
    public void Apply(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var match = _available.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var names = string.Join(", ", _available.Select(t => t.Name));
            throw new ArgumentException($"Unknown theme '{name}'. Available themes: {names}.", nameof(name));
        }
        Apply(match);
    }

    private IReadOnlyList<Theme> BuildAvailableThemes()
    {
        var builtIns = BuiltInThemes.All;
        var userThemes = _loader.LoadAll();
        var result = new List<Theme>(builtIns.Count + userThemes.Count);
        result.AddRange(builtIns);
        result.AddRange(userThemes);
        return result;
    }
}
