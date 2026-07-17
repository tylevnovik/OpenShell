using FluentAssertions;
using NSubstitute;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// VariableCompletionSource tests. Per ADR-0009.
/// Verifies dollar-prefixed variable name completion.
/// </summary>
public class VariableCompletionSourceTests
{
    private static IVariableRegistry MakeRegistry(params (string Name, object Value)[] vars)
    {
        var registry = Substitute.For<IVariableRegistry>();
        var list = vars
            .Select(v => new KeyValuePair<string, object>(v.Name, v.Value))
            .ToList();
        registry.List(Arg.Any<VariableScope?>()).Returns(list);
        return registry;
    }

    [Fact]
    public void GetCompletions_DollarOnly_ReturnsAllVariables()
    {
        var vars = MakeRegistry(("HOME", "/home/user"), ("PATH", "/usr/bin"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item $", 10));

        results.Should().HaveCount(2);
        results.Should().Contain(c => c.CompletionText == "$HOME");
        results.Should().Contain(c => c.CompletionText == "$PATH");
    }

    [Fact]
    public void GetCompletions_PrefixMatch_ReturnsMatchingVariables()
    {
        var vars = MakeRegistry(("HOME", "/home/user"), ("HOST", "localhost"), ("PATH", "/usr/bin"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item $HO", 11));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(c => c.CompletionText.StartsWith("$HO"));
    }

    [Fact]
    public void GetCompletions_ExactMatch_ReturnsSingleVariable()
    {
        var vars = MakeRegistry(("HOME", "/home/user"), ("PATH", "/usr/bin"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item $HOME", 13));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("$HOME");
    }

    [Fact]
    public void GetCompletions_CaseInsensitive_MatchesVariable()
    {
        var vars = MakeRegistry(("HOME", "/home/user"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item $ho", 11));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("$HOME");
    }

    [Fact]
    public void GetCompletions_TokenWithoutDollar_ReturnsEmpty()
    {
        var vars = MakeRegistry(("HOME", "/home/user"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item path", 12));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var vars = MakeRegistry(("HOME", "/home/user"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item $xyz", 12));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_VariableItem_HasVariableKind()
    {
        var vars = MakeRegistry(("HOME", "/home/user"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("get-item $HO", 11));

        results[0].Kind.Should().Be(CompletionKind.Variable);
    }

    [Fact]
    public void GetCompletions_DollarAtStart_ReturnsAllVariables()
    {
        var vars = MakeRegistry(("HOME", "/home/user"), ("PWD", "/tmp"));
        var source = new VariableCompletionSource(vars);

        var results = source.GetCompletions(new CompletionContext("$", 1));

        results.Should().HaveCount(2);
    }
}
