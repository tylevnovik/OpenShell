using FluentAssertions;
using NSubstitute;
using OpenShell.Commands;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// AliasCompletionSource tests. Per ADR-0009.
/// Verifies alias name completion at the command-name position.
/// </summary>
public class AliasCompletionSourceTests
{
    private static AliasDefinition MakeAlias(string name, string? description = null)
        => new()
        {
            Name = name,
            Command = "get-childitem",
            Source = AliasSource.Session,
            Description = description,
        };

    private static IAliasRegistry MakeRegistry(params AliasDefinition[] aliases)
    {
        var registry = Substitute.For<IAliasRegistry>();
        registry.List().Returns(aliases.ToList());
        return registry;
    }

    [Fact]
    public void GetCompletions_EmptyToken_ReturnsAllAliases()
    {
        var aliases = MakeRegistry(
            MakeAlias("ll", "List long"),
            MakeAlias("gci", "Get children"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("", 0));

        results.Should().HaveCount(2);
        results.Should().Contain(c => c.CompletionText == "ll");
        results.Should().Contain(c => c.CompletionText == "gci");
    }

    [Fact]
    public void GetCompletions_PrefixMatch_ReturnsMatchingAliases()
    {
        var aliases = MakeRegistry(
            MakeAlias("ll"),
            MakeAlias("gci"),
            MakeAlias("grep"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("g", 1));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(c => c.CompletionText.StartsWith("g", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCompletions_CaseInsensitive_MatchesAlias()
    {
        var aliases = MakeRegistry(MakeAlias("LL"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("ll", 2));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("LL");
    }

    [Fact]
    public void GetCompletions_NotAtStart_ReturnsEmpty()
    {
        var aliases = MakeRegistry(MakeAlias("ll"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("get-item -", 10));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var aliases = MakeRegistry(MakeAlias("ll"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("xyz", 3));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_AliasItem_HasAliasKind()
    {
        var aliases = MakeRegistry(MakeAlias("ll"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("l", 1));

        results[0].Kind.Should().Be(CompletionKind.Alias);
    }

    [Fact]
    public void GetCompletions_PreservesDescription()
    {
        var aliases = MakeRegistry(MakeAlias("ll", "List long format"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("l", 1));

        results[0].Description.Should().Be("List long format");
    }

    [Fact]
    public void GetCompletions_NullDescription_Accepted()
    {
        var aliases = MakeRegistry(MakeAlias("ll", null));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("l", 1));

        results[0].Description.Should().BeNull();
    }

    [Fact]
    public void GetCompletions_ExactMatch_ReturnsSingleAlias()
    {
        var aliases = MakeRegistry(MakeAlias("ll"), MakeAlias("gci"));
        var source = new AliasCompletionSource(aliases);

        var results = source.GetCompletions(new CompletionContext("ll", 2));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("ll");
    }
}
