using FluentAssertions;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// VariableEntry 单元测试。Per ADR-0047 §1.1.
/// </summary>
public class VariableEntryTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var entry = new VariableEntry(
            "x", 42, typeof(int),
            isPrivate: true, isConstant: false, isReadOnly: true);
        entry.Name.Should().Be("x");
        entry.Value.Should().Be(42);
        entry.DeclaredType.Should().Be(typeof(int));
        entry.IsPrivate.Should().BeTrue();
        entry.IsConstant.Should().BeFalse();
        entry.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Constructor_DefaultsAllFlagsToFalse()
    {
        var entry = new VariableEntry("y", "value");
        entry.IsPrivate.Should().BeFalse();
        entry.IsConstant.Should().BeFalse();
        entry.IsReadOnly.Should().BeFalse();
        entry.DeclaredType.Should().BeNull();
    }

    [Fact]
    public void Value_Setter_AllowsMutation()
    {
        var entry = new VariableEntry("counter", 0);
        entry.Value = 5;
        entry.Value.Should().Be(5);
    }

    [Fact]
    public void Init_Initializers_ApplyFlags()
    {
        // init-only props allow object initializer syntax.
        var entry = new VariableEntry("c", 0, isConstant: true)
        {
            IsPrivate = false,
            IsReadOnly = true,
        };
        entry.IsConstant.Should().BeTrue();
        entry.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Init_IsPrivate_CanBeSetViaInitializer()
    {
        var entry = new VariableEntry("p", "secret") { IsPrivate = true };
        entry.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Init_IsConstant_CanBeSetViaInitializer()
    {
        var entry = new VariableEntry("k", 42) { IsConstant = true };
        entry.IsConstant.Should().BeTrue();
    }

    [Fact]
    public void Constructor_AcceptsNullValue()
    {
        var entry = new VariableEntry("n", null);
        entry.Value.Should().BeNull();
        entry.Name.Should().Be("n");
    }
}
