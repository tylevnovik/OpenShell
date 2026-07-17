using FluentAssertions;
using OpenShell.KeyBindings;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.KeyBindings;

/// <summary>
/// Tests for KeyBindingService: default loading, user overrides, unbinds,
/// runtime register/unregister, reload, and BindingsChanged notifications.
/// Per ADR-0027 sections 3-5.
/// </summary>
public class KeyBindingServiceTests
{
    // ---- Constructor / defaults -----------------------------------------

    [Fact]
    public void Constructor_LoadsDefaults()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");

        service.Bindings.Should().HaveCount(DefaultKeyBindings.All.Count);
    }

    [Fact]
    public void Resolve_CopyItem_AnyContext()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");

        var gesture = new KeyGesture(KeyGestures.PrimaryModifier, "C");
        var resolved = service.Resolve(gesture, new KeyBindingContext());

        resolved.Should().NotBeNull();
        resolved!.CommandId.Should().Be("copy-item");
    }

    [Fact]
    public void Resolve_F5_Refresh()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");

        var resolved = service.Resolve(new KeyGesture(KeyModifiers.None, "F5"), new KeyBindingContext());
        resolved.Should().NotBeNull();
        resolved!.CommandId.Should().Be("refresh");
    }

    [Fact]
    public void Resolve_UnknownGesture_ReturnsNull()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");

        var resolved = service.Resolve(new KeyGesture(KeyModifiers.None, "F9"), new KeyBindingContext());
        resolved.Should().BeNull();
    }

    [Fact]
    public void Resolve_NavigateUp_OnlyWhenFocusPane()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var backspace = new KeyGesture(KeyModifiers.None, "Backspace");
        var paneCtx = new KeyBindingContext { FocusedElement = "pane" };
        var treeCtx = new KeyBindingContext { FocusedElement = "tree" };

        service.Resolve(backspace, paneCtx).Should().NotBeNull();
        service.Resolve(backspace, paneCtx)!.CommandId.Should().Be("navigate-up");
        service.Resolve(backspace, treeCtx).Should().BeNull();
    }

    [Fact]
    public void Resolve_AltUp_NavigateUp_WhenFocusPane()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var altUp = new KeyGesture(KeyModifiers.Alt, "Up");

        service.Resolve(altUp, new KeyBindingContext { FocusedElement = "pane" })!.CommandId
            .Should().Be("navigate-up");
        service.Resolve(altUp, new KeyBindingContext { FocusedElement = "console" })
            .Should().BeNull();
    }

    // ---- User overrides / unbinds ---------------------------------------

    [Fact]
    public void UserBinding_OverridesDefault_SameGestureAndWhen()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "Alt+Left"
command = "custom-back"
when = "focus:pane"
description = "Custom navigate back"
""";
        var service = CreateService(temp, toml);
        var altLeft = new KeyGesture(KeyModifiers.Alt, "Left");
        var paneCtx = new KeyBindingContext { FocusedElement = "pane" };

        var resolved = service.Resolve(altLeft, paneCtx);

        resolved.Should().NotBeNull();
        resolved!.CommandId.Should().Be("custom-back");
        resolved.Description.Should().Be("Custom navigate back");
        // No duplicate: only one Alt+Left + focus:pane binding remains.
        service.Bindings.Count(b => b.Gesture.Equals(altLeft) && b.When == "focus:pane")
            .Should().Be(1);
    }

    [Fact]
    public void UserUnbind_RemovesDefault()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "Alt+Left"
unbind = true
""";
        var service = CreateService(temp, toml);
        var altLeft = new KeyGesture(KeyModifiers.Alt, "Left");
        var paneCtx = new KeyBindingContext { FocusedElement = "pane" };

        service.Resolve(altLeft, paneCtx).Should().BeNull();
        service.Bindings.Should().NotContain(b => b.Gesture.Equals(altLeft));
    }

    [Fact]
    public void UserUnbind_WithWhen_RemovesOnlyMatching()
    {
        using var temp = new TempDir();
        // Alt+Left default has When focus:pane. Unbind only that scoped one.
        var toml = """
[[binding]]
gesture = "Alt+Left"
unbind = true
when = "focus:pane"
""";
        var service = CreateService(temp, toml);
        var altLeft = new KeyGesture(KeyModifiers.Alt, "Left");

        service.Bindings.Should().NotContain(b => b.Gesture.Equals(altLeft));
    }

    [Fact]
    public void UserBinding_NewGesture_Appended()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "F9"
command = "user-cmd"
description = "User added"
""";
        var service = CreateService(temp, toml);
        var f9 = new KeyGesture(KeyModifiers.None, "F9");

        var resolved = service.Resolve(f9, new KeyBindingContext());

        resolved.Should().NotBeNull();
        resolved!.CommandId.Should().Be("user-cmd");
        service.Bindings.Should().HaveCount(DefaultKeyBindings.All.Count + 1);
    }

    [Fact]
    public void Conflict_UserWins_NoDuplicate()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "Alt+Left"
command = "winner-back"
when = "focus:pane"
""";
        var service = CreateService(temp, toml);
        var altLeft = new KeyGesture(KeyModifiers.Alt, "Left");

        // Exactly one binding for that gesture+when, and it is the user's.
        service.Bindings.Count(b => b.Gesture.Equals(altLeft) && b.When == "focus:pane")
            .Should().Be(1);
        var resolved = service.Resolve(altLeft, new KeyBindingContext { FocusedElement = "pane" });
        resolved!.CommandId.Should().Be("winner-back");
    }

    [Fact]
    public void InvalidUserGesture_Skipped_NoThrow()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "Foo+Bar+"
command = "invalid"

[[binding]]
gesture = "F9"
command = "valid"
""";
        var service = CreateService(temp, toml);

        // Invalid entry skipped; valid entry loaded.
        service.Resolve(new KeyGesture(KeyModifiers.None, "F9"), new KeyBindingContext())!
            .CommandId.Should().Be("valid");
    }

    [Fact]
    public void UserBinding_NoCommand_Skipped()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "F9"
description = "no command here"
""";
        var service = CreateService(temp, toml);

        service.Resolve(new KeyGesture(KeyModifiers.None, "F9"), new KeyBindingContext())
            .Should().BeNull();
    }

    // ---- Runtime register / unregister ---------------------------------

    [Fact]
    public void Register_AddsBinding()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var f9 = new KeyGesture(KeyModifiers.None, "F9");

        service.Register(new KeyBinding(f9, "test-cmd", Description: "Test"));

        service.Resolve(f9, new KeyBindingContext())!.CommandId.Should().Be("test-cmd");
        service.Bindings.Should().Contain(b => b.CommandId == "test-cmd");
    }

    [Fact]
    public void Unregister_RemovesBinding()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var f9 = new KeyGesture(KeyModifiers.None, "F9");
        service.Register(new KeyBinding(f9, "test-cmd"));

        service.Unregister(f9);

        service.Resolve(f9, new KeyBindingContext()).Should().BeNull();
    }

    [Fact]
    public void Unregister_ScopedByWhen_RemovesOnlyMatching()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var g = new KeyGesture(KeyModifiers.None, "F9");
        service.Register(new KeyBinding(g, "global-cmd"));
        service.Register(new KeyBinding(g, "pane-cmd", When: "focus:pane"));

        service.Unregister(g, "focus:pane");

        service.Bindings.Should().NotContain(b => b.CommandId == "pane-cmd");
        service.Bindings.Should().Contain(b => b.CommandId == "global-cmd");
    }

    // ---- Reload ---------------------------------------------------------

    [Fact]
    public void ReloadUserBindings_ReReadsFile()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("kb.toml");
        File.WriteAllText(path, """
[[binding]]
gesture = "Alt+Left"
command = "first-back"
when = "focus:pane"
""");
        var service = new KeyBindingService(new KeyBindingFileLoader(path));
        var altLeft = new KeyGesture(KeyModifiers.Alt, "Left");
        var paneCtx = new KeyBindingContext { FocusedElement = "pane" };

        service.Resolve(altLeft, paneCtx)!.CommandId.Should().Be("first-back");

        File.WriteAllText(path, """
[[binding]]
gesture = "Alt+Left"
command = "reloaded-back"
when = "focus:pane"
""");
        service.ReloadUserBindings();

        service.Resolve(altLeft, paneCtx)!.CommandId.Should().Be("reloaded-back");
    }

    [Fact]
    public void ReloadUserBindings_RestoresDefaultsWhenFileEmptied()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("kb.toml");
        File.WriteAllText(path, """
[[binding]]
gesture = "Alt+Left"
unbind = true
""");
        var service = new KeyBindingService(new KeyBindingFileLoader(path));
        var altLeft = new KeyGesture(KeyModifiers.Alt, "Left");
        service.Resolve(altLeft, new KeyBindingContext { FocusedElement = "pane" }).Should().BeNull();

        File.WriteAllText(path, "");
        service.ReloadUserBindings();

        // Default Alt+Left restored.
        service.Resolve(altLeft, new KeyBindingContext { FocusedElement = "pane" })!
            .CommandId.Should().Be("navigate-back");
    }

    // ---- BindingsChanged ------------------------------------------------

    [Fact]
    public void BindingsChanged_FiresOnRegister()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var received = new List<IReadOnlyList<KeyBinding>>();
        service.BindingsChanged.Subscribe(received.Add);

        service.Register(new KeyBinding(new KeyGesture(KeyModifiers.None, "F9"), "x"));

        received.Should().HaveCount(1);
        received[0].Should().NotBeEmpty();
    }

    [Fact]
    public void BindingsChanged_FiresOnUnregister()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var f9 = new KeyGesture(KeyModifiers.None, "F9");
        service.Register(new KeyBinding(f9, "x"));
        var received = new List<IReadOnlyList<KeyBinding>>();
        service.BindingsChanged.Subscribe(received.Add);

        service.Unregister(f9);

        received.Should().HaveCount(1);
    }

    [Fact]
    public void BindingsChanged_FiresOnReload()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var received = new List<IReadOnlyList<KeyBinding>>();
        service.BindingsChanged.Subscribe(received.Add);

        service.ReloadUserBindings();

        received.Should().HaveCount(1);
    }

    [Fact]
    public void BindingsChanged_DoesNotFireOnUnregisterWhenNothingRemoved()
    {
        using var temp = new TempDir();
        var service = CreateService(temp, "");
        var received = new List<IReadOnlyList<KeyBinding>>();
        service.BindingsChanged.Subscribe(received.Add);

        service.Unregister(new KeyGesture(KeyModifiers.None, "F9"));

        received.Should().BeEmpty();
    }

    // ---- Helpers --------------------------------------------------------

    private static KeyBindingService CreateService(TempDir temp, string toml)
    {
        var path = temp.GetFullPath("kb.toml");
        File.WriteAllText(path, toml);
        return new KeyBindingService(new KeyBindingFileLoader(path));
    }
}
