namespace OpenShell.KeyBindings;

/// <summary>
/// A single keybinding: a gesture mapped to a command, optionally scoped by a When
/// expression. Per ADR-0027 section 2.
/// </summary>
/// <param name="Gesture">Gesture that triggers this binding.</param>
/// <param name="CommandId">Full name of the command to invoke.</param>
/// <param name="Args">Optional command arguments.</param>
/// <param name="When">When expression source; null or empty means always active.</param>
/// <param name="Description">Human-readable description.</param>
public sealed record KeyBinding(
    KeyGesture Gesture,
    string CommandId,
    IReadOnlyDictionary<string, string>? Args = null,
    string? When = null,
    string? Description = null)
{
    /// <summary>
    /// Compiled When expression. Null or empty When yields an always-true expression.
    /// Re-parsed on each access (cheap for fewer than 200 bindings).
    /// </summary>
    public global::OpenShell.When.WhenExpression WhenExpression
        => global::OpenShell.When.WhenExpression.Parse(When);
}
