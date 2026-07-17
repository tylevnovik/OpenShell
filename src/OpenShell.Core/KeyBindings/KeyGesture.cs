namespace OpenShell.KeyBindings;

/// <summary>
/// Modifier keys for a keybinding gesture. Per ADR-0027 section 2.
/// Core does not reference Avalonia; this enum mirrors the subset of
/// cross-platform modifiers needed for keyboard shortcuts.
/// </summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Shift key.</summary>
    Shift = 1,

    /// <summary>Control (Ctrl) key.</summary>
    Control = 2,

    /// <summary>Alt (Option) key.</summary>
    Alt = 4,

    /// <summary>Meta / Cmd / Win key.</summary>
    Meta = 8,
}

/// <summary>
/// An immutable key gesture: modifiers plus a normalized key name. Per ADR-0027 section 2.
/// The Key is normalized to a PascalCase-like form (first character upper, the rest lower)
/// e.g. P, F5, Enter, Backspace.
/// </summary>
public sealed record KeyGesture
{
    /// <summary>Active modifier flags.</summary>
    public KeyModifiers Modifiers { get; init; }

    /// <summary>Normalized key name (first char upper, rest lower).</summary>
    public string Key { get; init; }

    /// <summary>
    /// Construct a gesture, normalizing the key name.
    /// </summary>
    /// <param name="modifiers">Active modifiers.</param>
    /// <param name="key">Key name; will be normalized.</param>
    public KeyGesture(KeyModifiers modifiers, string key)
    {
        Modifiers = modifiers;
        Key = NormalizeKey(key);
    }

    /// <summary>
    /// Human-readable form e.g. Ctrl+Shift+P, F5, Alt+Enter.
    /// </summary>
    public string DisplayString => KeyGestureParser.Format(this);

    /// <summary>
    /// Normalize a key name: first character upper, the rest lower.
    /// Empty input is returned unchanged.
    /// </summary>
    /// <param name="key">Raw key name.</param>
    /// <returns>Normalized key name.</returns>
    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return key ?? string.Empty;
        return char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant();
    }
}
