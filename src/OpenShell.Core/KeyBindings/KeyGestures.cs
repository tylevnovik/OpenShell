namespace OpenShell.KeyBindings;

/// <summary>
/// Static helpers for cross-platform key gestures. Per ADR-0027 section 2.
/// The primary action modifier is Cmd (Meta) on macOS and Ctrl (Control) elsewhere,
/// so default bindings adapt to platform conventions automatically.
/// </summary>
public static class KeyGestures
{
    /// <summary>
    /// The primary action modifier for the current OS:
    /// Cmd (Meta) on macOS, Ctrl (Control) on all other platforms.
    /// </summary>
    public static KeyModifiers PrimaryModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
}
