using FluentAssertions;
using OpenShell.Variables;
using NSubstitute;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// VariableExpander 单元测试。Per ADR-0042, ADR-0033.
/// </summary>
public class VariableExpanderTests
{
    private static IVariableRegistry CreateVarRegistry()
    {
        var registry = Substitute.For<IVariableRegistry>();
        registry.Resolve("name").Returns("Alice");
        registry.Resolve("age").Returns(30L);
        registry.Resolve("env:PATH").Returns("/usr/bin");
        registry.Resolve("missing").Returns((object?)null);
        return registry;
    }

    [Fact]
    public void TryResolve_DollarName_ReturnsTrueWithValue()
    {
        var vars = CreateVarRegistry();
        var ok = VariableExpander.TryResolve("$name", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("Alice");
    }

    [Fact]
    public void TryResolve_DollarBraceName_ReturnsTrueWithValue()
    {
        var vars = CreateVarRegistry();
        var ok = VariableExpander.TryResolve("${name}", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("Alice");
    }

    [Fact]
    public void TryResolve_DollarEnvName_ReturnsTrueWithValue()
    {
        var vars = CreateVarRegistry();
        var ok = VariableExpander.TryResolve("$env:PATH", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("/usr/bin");
    }

    [Fact]
    public void TryResolve_PlainText_ReturnsFalse()
    {
        var vars = CreateVarRegistry();
        var ok = VariableExpander.TryResolve("hello world", vars, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryResolve_EmptyString_ReturnsFalse()
    {
        var vars = CreateVarRegistry();
        VariableExpander.TryResolve("", vars, out _).Should().BeFalse();
        VariableExpander.TryResolve("   ", vars, out _).Should().BeFalse();
    }

    [Fact]
    public void Expand_DollarName_ReplacesInPlace()
    {
        var vars = CreateVarRegistry();
        var result = VariableExpander.Expand("hello $name!", vars);
        result.Should().Be("hello Alice!");
    }

    [Fact]
    public void Expand_DollarBraceName_ReplacesInPlace()
    {
        var vars = CreateVarRegistry();
        var result = VariableExpander.Expand("${name} is ${age}", vars);
        result.Should().Be("Alice is 30");
    }

    [Fact]
    public void Expand_SingleQuotedString_NoInterpolation()
    {
        var vars = CreateVarRegistry();
        var result = VariableExpander.Expand("'hello $name'", vars);
        result.Should().Be("hello $name");
    }

    [Fact]
    public void Expand_UndefinedVariable_ReplacesWithEmpty()
    {
        var vars = CreateVarRegistry();
        var result = VariableExpander.Expand("[$missing]", vars);
        result.Should().Be("[]");
    }

    [Fact]
    public void Expand_EmptyInput_ReturnsEmpty()
    {
        var vars = CreateVarRegistry();
        VariableExpander.Expand("", vars).Should().Be("");
    }

    [Fact]
    public void Expand_NoVariables_ReturnsInputUnchanged()
    {
        var vars = CreateVarRegistry();
        VariableExpander.Expand("plain text without vars", vars)
            .Should().Be("plain text without vars");
    }

    [Fact]
    public void Expand_DollarEnvName_ReplacesInPlace()
    {
        var vars = CreateVarRegistry();
        var result = VariableExpander.Expand("PATH=$env:PATH", vars);
        result.Should().Be("PATH=/usr/bin");
    }

    [Fact]
    public void Expand_MultipleOccurrences_AllReplaced()
    {
        var vars = CreateVarRegistry();
        var result = VariableExpander.Expand("$name-$name", vars);
        result.Should().Be("Alice-Alice");
    }

    [Fact]
    public void TryResolve_VariableWithScopeModifier_ReturnsTrue()
    {
        var vars = CreateVarRegistry();
        vars.Resolve(Arg.Is<string>(n => n.StartsWith("global:"))).Returns("global-value");
        var ok = VariableExpander.TryResolve("$global:name", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("global-value");
    }

    [Fact]
    public void TryResolve_DollarVarProperty_ReturnsPropertyValue()
    {
        var vars = CreateVarRegistry();
        var person = new { Name = "Alice", Age = 30 };
        vars.Resolve("person").Returns(person);
        var ok = VariableExpander.TryResolve("$person.Name", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("Alice");
    }

    [Fact]
    public void TryResolve_DollarBraceVarProperty_ReturnsPropertyValue()
    {
        var vars = CreateVarRegistry();
        var person = new { Name = "Bob" };
        vars.Resolve("person").Returns(person);
        var ok = VariableExpander.TryResolve("${person}.Name", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("Bob");
    }

    [Fact]
    public void TryResolve_DollarArrayIndex_ReturnsElement()
    {
        var vars = CreateVarRegistry();
        vars.Resolve("arr").Returns(new[] { "x", "y", "z" });
        var ok = VariableExpander.TryResolve("$arr[0]", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("x");
    }

    [Fact]
    public void TryResolve_DollarArrayNegativeIndex_ReturnsLastElement()
    {
        var vars = CreateVarRegistry();
        vars.Resolve("arr").Returns(new[] { "x", "y", "z" });
        var ok = VariableExpander.TryResolve("$arr[-1]", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("z");
    }

    [Fact]
    public void TryResolve_DollarDictionaryIndex_ReturnsValueByKey()
    {
        var vars = CreateVarRegistry();
        var dict = new System.Collections.Hashtable(System.StringComparer.Ordinal)
        {
            ["key"] = "value-from-dict",
        };
        vars.Resolve("h").Returns(dict);
        var ok = VariableExpander.TryResolve("$h[\"key\"]", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be("value-from-dict");
    }

    [Fact]
    public void Expand_DollarVarProperty_ReplacesInPlace()
    {
        var vars = CreateVarRegistry();
        var person = new { Name = "Carol" };
        vars.Resolve("person").Returns(person);
        var result = VariableExpander.Expand("hello $person.Name!", vars);
        result.Should().Be("hello Carol!");
    }

    [Fact]
    public void Expand_DollarArrayIndex_ReplacesInPlace()
    {
        var vars = CreateVarRegistry();
        vars.Resolve("arr").Returns(new[] { "first", "second" });
        var result = VariableExpander.Expand("zeroth: $arr[0]", vars);
        result.Should().Be("zeroth: first");
    }

    [Fact]
    public void Expand_DollarBraceVarProperty_ReplacesInPlace()
    {
        var vars = CreateVarRegistry();
        var person = new { Name = "Dave" };
        vars.Resolve("person").Returns(person);
        var result = VariableExpander.Expand("name: ${person}.Name", vars);
        result.Should().Be("name: Dave");
    }

    [Fact]
    public void Expand_DollarSubExpression_Throws_NotSupported()
    {
        var vars = CreateVarRegistry();
        var act = () => VariableExpander.Expand("count: $(1 + 2)", vars);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void TryResolve_DollarBraceVarChainedIndex_ReturnsElement()
    {
        var vars = CreateVarRegistry();
        // Outer array of arrays.
        vars.Resolve("matrix").Returns(new[] { new[] { 1, 2 }, new[] { 3, 4 } });
        var ok = VariableExpander.TryResolve("$matrix[1][0]", vars, out var value);
        ok.Should().BeTrue();
        value.Should().Be(3);
    }
}
