using FluentAssertions;
using OpenShell.KeyBindings;
using Xunit;

namespace OpenShell.Core.Tests.KeyBindings;

/// <summary>
/// Tests for KeyGestures cross-platform helper. Per ADR-0027 section 2.
/// </summary>
public class KeyGesturesTests
{
    [Fact]
    public void PrimaryModifier_ReturnsValidEnumValue()
    {
        // Cmd (Meta) on macOS, Ctrl (Control) elsewhere. Both are valid single flags.
        var mod = KeyGestures.PrimaryModifier;
        mod.Should().BeOneOf(KeyModifiers.Control, KeyModifiers.Meta);
    }

    [Fact]
    public void PrimaryModifier_IsSingleFlag()
    {
        var mod = KeyGestures.PrimaryModifier;
        // A single flag has exactly one bit set.
        long bits = (long)mod;
        (bits != 0 && (bits & (bits - 1)) == 0).Should().BeTrue(
            "PrimaryModifier should be exactly one flag, not a combination");
    }
}
