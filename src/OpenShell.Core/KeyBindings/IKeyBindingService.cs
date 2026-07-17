namespace OpenShell.KeyBindings;

/// <summary>
/// Resolves keybindings to commands at runtime. Per ADR-0027 section 3.
/// Merges built-in defaults with user customizations and resolves a gesture
/// against the current KeyBindingContext using When expressions.
/// </summary>
public interface IKeyBindingService
{
    /// <summary>Current merged bindings (defaults plus user overrides).</summary>
    IReadOnlyList<KeyBinding> Bindings { get; }

    /// <summary>
    /// Resolve the first binding matching the gesture whose When expression
    /// evaluates true against the context. Returns null when no match exists.
    /// </summary>
    /// <param name="gesture">Gesture to resolve.</param>
    /// <param name="context">Current keybinding context.</param>
    /// <returns>Matching binding, or null.</returns>
    KeyBinding? Resolve(KeyGesture gesture, KeyBindingContext context);

    /// <summary>Register a new binding at runtime, firing BindingsChanged.</summary>
    /// <param name="binding">Binding to add.</param>
    void Register(KeyBinding binding);

    /// <summary>
    /// Remove all bindings matching the gesture, optionally scoped by When.
    /// When <paramref name="when"/> is null, all bindings for the gesture are removed.
    /// Fires BindingsChanged when at least one binding was removed.
    /// </summary>
    /// <param name="gesture">Gesture to remove.</param>
    /// <param name="when">Optional When clause to scope the removal.</param>
    void Unregister(KeyGesture gesture, string? when = null);

    /// <summary>Re-load defaults plus the user file, firing BindingsChanged.</summary>
    void ReloadUserBindings();

    /// <summary>Observable stream of binding-list snapshots on every change.</summary>
    IObservable<IReadOnlyList<KeyBinding>> BindingsChanged { get; }
}
