using FluentAssertions;
using OpenShell.Completion;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// CompletionContext record creation and equality. Per ADR-0009.
/// </summary>
public class CompletionContextTests
{
    [Fact]
    public void Constructor_PreservesInputAndCursor()
    {
        var ctx = new CompletionContext("get-childitem -path", 6);

        ctx.Input.Should().Be("get-childitem -path");
        ctx.CursorPosition.Should().Be(6);
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var a = new CompletionContext("hello world", 5);
        var b = new CompletionContext("hello world", 5);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentInput_ReturnsFalse()
    {
        var a = new CompletionContext("abc", 1);
        var b = new CompletionContext("xyz", 1);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Equals_DifferentCursor_ReturnsFalse()
    {
        var a = new CompletionContext("hello", 3);
        var b = new CompletionContext("hello", 4);

        a.Should().NotBe(b);
    }

    [Fact]
    public void WithExpression_ChangesCursor()
    {
        var original = new CompletionContext("input", 2);
        var moved = original with { CursorPosition = 4 };

        moved.CursorPosition.Should().Be(4);
        moved.Input.Should().Be("input");
    }

    [Fact]
    public void Constructor_AcceptsZeroCursor()
    {
        var ctx = new CompletionContext("abc", 0);

        ctx.CursorPosition.Should().Be(0);
    }

    [Fact]
    public void Constructor_AcceptsCursorAtEnd()
    {
        var ctx = new CompletionContext("abc", 3);

        ctx.CursorPosition.Should().Be(3);
    }
}
