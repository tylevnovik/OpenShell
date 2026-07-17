using System.Reactive.Subjects;

namespace OpenShell.KeyBindings;

/// <summary>
/// Default implementation of <see cref="IKeyBindingService"/>. Per ADR-0027 sections 3-5.
/// Loads built-in defaults, applies user overrides and unbinds from keybindings.toml,
/// and resolves gestures against a KeyBindingContext using When expressions.
/// </summary>
public sealed class KeyBindingService : IKeyBindingService
{
    private readonly KeyBindingFileLoader _loader;
    private readonly Subject<IReadOnlyList<KeyBinding>> _changed = new();
    private List<KeyBinding> _bindings;

    /// <summary>
    /// Construct the service, loading defaults then applying user overrides.
    /// </summary>
    /// <param name="loader">Optional file loader; defaults to the user-global file.</param>
    public KeyBindingService(KeyBindingFileLoader? loader = null)
    {
        _loader = loader ?? new KeyBindingFileLoader();
        _bindings = BuildBindings();
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyBinding> Bindings => _bindings;

    /// <inheritdoc />
    public IObservable<IReadOnlyList<KeyBinding>> BindingsChanged => _changed;

    /// <inheritdoc />
    public KeyBinding? Resolve(KeyGesture gesture, KeyBindingContext context)
    {
        var ctx = context.ToDictionary();
        // User bindings are appended after defaults and replaced in-place where they
        // override; reverse iteration gives user bindings priority (last loaded wins).
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            var b = _bindings[i];
            if (!b.Gesture.Equals(gesture)) continue;
            if (b.WhenExpression.Evaluate(ctx)) return b;
        }
        return null;
    }

    /// <inheritdoc />
    public void Register(KeyBinding binding)
    {
        _bindings.Add(binding);
        _changed.OnNext(_bindings);
    }

    /// <inheritdoc />
    public void Unregister(KeyGesture gesture, string? when = null)
    {
        int removed = _bindings.RemoveAll(b => b.Gesture.Equals(gesture) && MatchesWhen(b.When, when));
        if (removed > 0) _changed.OnNext(_bindings);
    }

    /// <inheritdoc />
    public void ReloadUserBindings()
    {
        _bindings = BuildBindings();
        _changed.OnNext(_bindings);
    }

    private List<KeyBinding> BuildBindings()
    {
        var result = new List<KeyBinding>(DefaultKeyBindings.All);
        var userBindings = _loader.Load();

        foreach (var u in userBindings)
        {
            KeyGesture gesture;
            try
            {
                gesture = KeyGestureParser.Parse(u.GestureText);
            }
            catch (ArgumentException)
            {
                Console.Error.WriteLine($"[warn] invalid gesture '{u.GestureText}' in keybindings; skipped.");
                continue;
            }

            if (u.Unbind)
            {
                // Unbind by gesture, scoped by When only when the entry provides one.
                bool scopeByWhen = !string.IsNullOrWhiteSpace(u.When);
                result.RemoveAll(b => b.Gesture.Equals(gesture) && (!scopeByWhen || MatchesWhen(b.When, u.When)));
                continue;
            }

            if (string.IsNullOrEmpty(u.Command))
            {
                Console.Error.WriteLine($"[warn] user binding '{u.GestureText}' has no command; skipped.");
                continue;
            }

            var binding = new KeyBinding(
                Gesture: gesture,
                CommandId: u.Command!,
                Args: u.Args,
                When: u.When,
                Description: u.Description);

            int idx = result.FindIndex(b => b.Gesture.Equals(gesture) && MatchesWhen(b.When, u.When));
            if (idx >= 0)
            {
                Console.Error.WriteLine(
                    $"[warn] keybinding conflict: user override replaces default for '{u.GestureText}'.");
                result[idx] = binding;
            }
            else
            {
                result.Add(binding);
            }
        }

        return result;
    }

    private static bool MatchesWhen(string? existing, string? requested)
    {
        var e = string.IsNullOrWhiteSpace(existing) ? null : existing;
        var r = string.IsNullOrWhiteSpace(requested) ? null : requested;
        return string.Equals(e, r, StringComparison.Ordinal);
    }
}
