using FluentAssertions;
using OpenShell.KeyBindings;
using Xunit;

namespace OpenShell.Core.Tests.KeyBindings;

/// <summary>
/// Tests for DefaultKeyBindings table. Per ADR-0027 section 2.
/// </summary>
public class DefaultKeyBindingsTests
{
    [Fact]
    public void All_ReturnsNonEmptyList()
    {
        DefaultKeyBindings.All.Should().NotBeEmpty();
        DefaultKeyBindings.All.Should().HaveCountGreaterThan(10);
    }

    [Fact]
    public void All_ContainsCopyItem_OnPrimaryModifier()
    {
        var gesture = new KeyGesture(KeyGestures.PrimaryModifier, "C");
        var binding = DefaultKeyBindings.All.FirstOrDefault(b => b.Gesture.Equals(gesture));
        binding.Should().NotBeNull();
        binding!.CommandId.Should().Be("copy-item");
        binding.Description.Should().Be("Copy selected items");
    }

    [Fact]
    public void All_ContainsRefresh_F5()
    {
        var gesture = new KeyGesture(KeyModifiers.None, "F5");
        var binding = DefaultKeyBindings.All.FirstOrDefault(b => b.Gesture.Equals(gesture));
        binding.Should().NotBeNull();
        binding!.CommandId.Should().Be("refresh");
    }

    [Fact]
    public void All_ContainsShowCommandPalette_CtrlShiftP()
    {
        var gesture = new KeyGesture(KeyGestures.PrimaryModifier | KeyModifiers.Shift, "P");
        var binding = DefaultKeyBindings.All.FirstOrDefault(b => b.Gesture.Equals(gesture));
        binding.Should().NotBeNull();
        binding!.CommandId.Should().Be("show-command-palette");
    }

    [Fact]
    public void All_ContainsNewTab_CtrlT()
    {
        var gesture = new KeyGesture(KeyGestures.PrimaryModifier, "T");
        var binding = DefaultKeyBindings.All.FirstOrDefault(b => b.Gesture.Equals(gesture));
        binding.Should().NotBeNull();
        binding!.CommandId.Should().Be("new-tab");
    }

    [Fact]
    public void All_NavigateUpHasTwoGestures()
    {
        // Backspace and Alt+Up both map to navigate-up (focus:pane).
        var backspace = new KeyGesture(KeyModifiers.None, "Backspace");
        var altUp = new KeyGesture(KeyModifiers.Alt, "Up");
        DefaultKeyBindings.All.Where(b => b.CommandId == "navigate-up")
            .Should().HaveCount(2);
        DefaultKeyBindings.All.Should().Contain(b => b.Gesture.Equals(backspace) && b.CommandId == "navigate-up");
        DefaultKeyBindings.All.Should().Contain(b => b.Gesture.Equals(altUp) && b.CommandId == "navigate-up");
    }

    [Fact]
    public void All_PaneScopedBindingsHaveFocusPaneWhen()
    {
        // Bindings scoped to the pane use the When expression focus:pane.
        var paneBindings = DefaultKeyBindings.All.Where(b => b.When == "focus:pane").ToList();
        paneBindings.Should().NotBeEmpty();
        paneBindings.Should().AllSatisfy(b => b.When.Should().Be("focus:pane"));
    }

    [Fact]
    public void All_GlobalBindingsHaveNullWhen()
    {
        // Bindings active in any context have null When.
        var copyItem = DefaultKeyBindings.All.Single(b => b.CommandId == "copy-item");
        copyItem.When.Should().BeNullOrEmpty();
        var refresh = DefaultKeyBindings.All.Single(b => b.CommandId == "refresh");
        refresh.When.Should().BeNullOrEmpty();
    }

    [Fact]
    public void All_EachBindingHasDescription()
    {
        // Per ADR constraint: every default binding carries a description.
        foreach (var b in DefaultKeyBindings.All)
        {
            b.Description.Should().NotBeNullOrEmpty(
                $"binding '{b.Gesture.DisplayString}' -> '{b.CommandId}' should have a description");
        }
    }

    [Fact]
    public void All_EachBindingHasCommandId()
    {
        DefaultKeyBindings.All.Should().AllSatisfy(b =>
            b.CommandId.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public void All_NoDuplicateGestureAndWhen()
    {
        // No two default bindings share the same gesture AND When clause.
        var keys = DefaultKeyBindings.All
            .Select(b => (Gesture: b.Gesture, When: b.When ?? ""))
            .ToList();
        var distinct = keys.Distinct().ToList();
        keys.Should().HaveCount(distinct.Count, "no duplicate gesture+when pairs");
    }
}
