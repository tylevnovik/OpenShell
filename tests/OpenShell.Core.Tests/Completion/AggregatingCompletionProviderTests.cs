using FluentAssertions;
using NSubstitute;
using OpenShell.Completion;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// AggregatingCompletionProvider tests. Per ADR-0009.
/// Verifies that the aggregator concatenates results from all registered sources in order.
/// </summary>
public class AggregatingCompletionProviderTests
{
    [Fact]
    public void GetCompletions_NoSources_ReturnsEmpty()
    {
        var provider = new AggregatingCompletionProvider(Array.Empty<ICompletionSource>());
        var ctx = new CompletionContext("test", 4);

        var results = provider.GetCompletions(ctx);

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_SingleSource_ReturnsSourceResults()
    {
        var source = Substitute.For<ICompletionSource>();
        var ctx = new CompletionContext("ab", 2);
        var items = new List<CompletionItem>
        {
            new("alpha", "alpha", null, CompletionKind.Command),
            new("beta", "beta", null, CompletionKind.Command),
        };
        source.GetCompletions(ctx).Returns(items);

        var provider = new AggregatingCompletionProvider(new[] { source });

        var results = provider.GetCompletions(ctx);

        results.Should().HaveCount(2);
        results[0].DisplayText.Should().Be("alpha");
        results[1].DisplayText.Should().Be("beta");
    }

    [Fact]
    public void GetCompletions_MultipleSources_ConcatenatesInOrder()
    {
        var ctx = new CompletionContext("x", 1);
        var source1 = Substitute.For<ICompletionSource>();
        source1.GetCompletions(ctx).Returns(new List<CompletionItem>
        {
            new("first", "first", null, CompletionKind.Command),
        });
        var source2 = Substitute.For<ICompletionSource>();
        source2.GetCompletions(ctx).Returns(new List<CompletionItem>
        {
            new("second", "second", null, CompletionKind.Alias),
            new("third", "third", null, CompletionKind.History),
        });

        var provider = new AggregatingCompletionProvider(new[] { source1, source2 });

        var results = provider.GetCompletions(ctx);

        results.Should().HaveCount(3);
        results[0].DisplayText.Should().Be("first");
        results[1].DisplayText.Should().Be("second");
        results[2].DisplayText.Should().Be("third");
    }

    [Fact]
    public void GetCompletions_SourceReturnsEmpty_DoesNotAffectOthers()
    {
        var ctx = new CompletionContext("x", 1);
        var emptySource = Substitute.For<ICompletionSource>();
        emptySource.GetCompletions(ctx).Returns(Array.Empty<CompletionItem>());
        var realSource = Substitute.For<ICompletionSource>();
        realSource.GetCompletions(ctx).Returns(new List<CompletionItem>
        {
            new("real", "real", null, CompletionKind.Command),
        });

        var provider = new AggregatingCompletionProvider(new[] { emptySource, realSource });

        var results = provider.GetCompletions(ctx);

        results.Should().HaveCount(1);
        results[0].DisplayText.Should().Be("real");
    }

    [Fact]
    public void Constructor_NullSources_Throws()
    {
        var act = () => new AggregatingCompletionProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetCompletions_PreservesSourceRegistrationOrder()
    {
        var ctx = new CompletionContext("x", 1);
        var sourceA = Substitute.For<ICompletionSource>();
        sourceA.GetCompletions(ctx).Returns(new List<CompletionItem>
        {
            new("a", "a", null, CompletionKind.Command),
        });
        var sourceB = Substitute.For<ICompletionSource>();
        sourceB.GetCompletions(ctx).Returns(new List<CompletionItem>
        {
            new("b", "b", null, CompletionKind.Alias),
        });

        var providerBA = new AggregatingCompletionProvider(new[] { sourceB, sourceA });
        var resultsBA = providerBA.GetCompletions(ctx);
        resultsBA[0].DisplayText.Should().Be("b");
        resultsBA[1].DisplayText.Should().Be("a");
    }
}
