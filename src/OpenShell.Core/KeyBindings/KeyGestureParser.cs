using System.Text;

namespace OpenShell.KeyBindings;

/// <summary>
/// Parses and formats key gesture strings. Per ADR-0027 section 2.
/// Accepts modifier aliases: Ctrl or Control, Shift, Alt or Option, Cmd or Meta or Win.
/// The last token is always the key; preceding tokens are modifiers.
/// </summary>
public static class KeyGestureParser
{
    private static readonly Dictionary<string, KeyModifiers> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = KeyModifiers.Control,
        ["control"] = KeyModifiers.Control,
        ["shift"] = KeyModifiers.Shift,
        ["alt"] = KeyModifiers.Alt,
        ["option"] = KeyModifiers.Alt,
        ["cmd"] = KeyModifiers.Meta,
        ["meta"] = KeyModifiers.Meta,
        ["win"] = KeyModifiers.Meta,
    };

    /// <summary>
    /// Parse a gesture string like Ctrl+Shift+P or F5 into a KeyGesture.
    /// </summary>
    /// <param name="text">Gesture text, split on plus signs.</param>
    /// <returns>Parsed gesture with normalized key.</returns>
    /// <exception cref="ArgumentException">Thrown when input is empty or malformed.</exception>
    public static KeyGesture Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Gesture text must not be empty.", nameof(text));

        var parts = text.Split('+');
        var modifiers = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var token = parts[i].Trim();
            if (token.Length == 0)
                throw new ArgumentException($"Empty modifier token in gesture '{text}'.", nameof(text));
            if (!ModifierAliases.TryGetValue(token, out var mod))
                throw new ArgumentException($"Unknown modifier '{token}' in gesture '{text}'.", nameof(text));
            modifiers |= mod;
        }

        var keyToken = parts[^1].Trim();
        if (keyToken.Length == 0)
            throw new ArgumentException($"Missing key in gesture '{text}'.", nameof(text));
        if (ModifierAliases.ContainsKey(keyToken))
            throw new ArgumentException($"Gesture '{text}' ends with a modifier; missing a key.", nameof(text));

        return new KeyGesture(modifiers, KeyGesture.NormalizeKey(keyToken));
    }

    /// <summary>
    /// Format a KeyGesture into its canonical string form, the inverse of Parse.
    /// Modifier order: Ctrl, Shift, Alt, Cmd.
    /// </summary>
    /// <param name="gesture">Gesture to format.</param>
    /// <returns>Canonical gesture string e.g. Ctrl+Shift+P.</returns>
    public static string Format(KeyGesture gesture)
    {
        var sb = new StringBuilder();
        if (gesture.Modifiers.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
        if (gesture.Modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        if (gesture.Modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
        if (gesture.Modifiers.HasFlag(KeyModifiers.Meta)) sb.Append("Cmd+");
        sb.Append(gesture.Key);
        return sb.ToString();
    }
}
