namespace OpenShell.KeyBindings;

/// <summary>
/// Built-in default keybindings. Per ADR-0027 section 2 table.
/// Ctrl-based bindings use <see cref="KeyGestures.PrimaryModifier"/> so macOS
/// uses Cmd and other platforms use Ctrl.
/// </summary>
public static class DefaultKeyBindings
{
    /// <summary>
    /// All built-in default keybindings, in registration order.
    /// </summary>
    public static IReadOnlyList<KeyBinding> All { get; } = Build();

    private static IReadOnlyList<KeyBinding> Build()
    {
        var p = KeyGestures.PrimaryModifier;
        var list = new List<KeyBinding>
        {
            new(new KeyGesture(p, "C"), "copy-item", Description: "Copy selected items"),
            new(new KeyGesture(p, "V"), "paste", Description: "Paste"),
            new(new KeyGesture(p, "X"), "cut-item", Description: "Cut selected items"),
            new(new KeyGesture(p, "A"), "select-all", When: "focus:pane", Description: "Select all"),
            new(new KeyGesture(p, "Z"), "undo", Description: "Undo"),
            new(new KeyGesture(p, "Y"), "redo", Description: "Redo"),
            new(new KeyGesture(KeyModifiers.None, "F5"), "refresh", Description: "Refresh"),
            new(new KeyGesture(KeyModifiers.None, "Backspace"), "navigate-up", When: "focus:pane", Description: "Navigate up"),
            new(new KeyGesture(KeyModifiers.Alt, "Up"), "navigate-up", When: "focus:pane", Description: "Navigate up"),
            new(new KeyGesture(KeyModifiers.Alt, "Left"), "navigate-back", When: "focus:pane", Description: "Navigate back"),
            new(new KeyGesture(KeyModifiers.Alt, "Right"), "navigate-forward", When: "focus:pane", Description: "Navigate forward"),
            new(new KeyGesture(p, "T"), "new-tab", Description: "New tab"),
            new(new KeyGesture(p, "W"), "close-tab", Description: "Close tab"),
            new(new KeyGesture(p, "Tab"), "next-tab", Description: "Next tab"),
            new(new KeyGesture(p | KeyModifiers.Shift, "Tab"), "prev-tab", Description: "Previous tab"),
            new(new KeyGesture(p, "L"), "focus-location", When: "focus:pane", Description: "Focus location box"),
            new(new KeyGesture(p | KeyModifiers.Shift, "P"), "show-command-palette", Description: "Show command palette"),
            new(new KeyGesture(KeyModifiers.None, "F1"), "help", Description: "Help"),
            new(new KeyGesture(KeyModifiers.None, "F2"), "rename", When: "focus:pane", Description: "Rename"),
            new(new KeyGesture(KeyModifiers.None, "Delete"), "remove-item", When: "focus:pane", Description: "Remove item"),
            new(new KeyGesture(KeyModifiers.None, "Enter"), "open", When: "focus:pane", Description: "Open"),
            new(new KeyGesture(KeyModifiers.None, "Space"), "quick-preview", When: "focus:pane", Description: "Quick preview"),
            new(new KeyGesture(p | KeyModifiers.Shift, "N"), "new-folder", When: "focus:pane", Description: "New folder"),
            new(new KeyGesture(p, "H"), "toggle-hidden", When: "focus:pane", Description: "Toggle hidden files"),
        };
        return list;
    }
}
