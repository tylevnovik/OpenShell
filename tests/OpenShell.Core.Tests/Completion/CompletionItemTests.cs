using FluentAssertions;
using OpenShell.Completion;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// CompletionItem record equality and defaults. Per ADR-0009.
/// </summary>
public class CompletionItemTests
{
    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var a = new CompletionItem("get-childitem", "get-childitem", "List children", CompletionKind.Command);
        var b = new CompletionItem("get-childitem", "get-childitem", "List children", CompletionKind.Command);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentDisplayText_ReturnsFalse()
    {
        var a = new CompletionItem("a", "a");
        var b = new CompletionItem("b", "b");

        a.Should().NotBe(b);
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentKind_ReturnsFalse()
    {
        var a = new CompletionItem("x", "x", null, CompletionKind.Command);
        var b = new CompletionItem("x", "x", null, CompletionKind.Alias);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Defaults_DescriptionIsNull_KindIsText()
    {
        var item = new CompletionItem("foo", "foo");

        item.Description.Should().BeNull();
        item.Kind.Should().Be(CompletionKind.Text);
    }

    [Fact]
    public void Construction_PreservesAllValues()
    {
        var item = new CompletionItem("display", "complete", "desc", CompletionKind.Path);

        item.DisplayText.Should().Be("display");
        item.CompletionText.Should().Be("complete");
        item.Description.Should().Be("desc");
        item.Kind.Should().Be(CompletionKind.Path);
    }

    [Fact]
    public void WithExpression_ProducesEqualRecord()
    {
        var original = new CompletionItem("a", "a");
        var copy = original with { };

        copy.Should().Be(original);
    }
}
