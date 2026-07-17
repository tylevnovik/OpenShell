using FluentAssertions;
using OpenShell.Completion;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// CompletionParser tokenization tests. Per ADR-0009.
/// Verifies token extraction, command-name detection, and tolerant handling of edge cases.
/// </summary>
public class CompletionParserTests
{
    [Fact]
    public void Parse_EmptyInput_AtStartTrue()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("", 0));

        parsed.Token.Should().Be("");
        parsed.AtStart.Should().BeTrue();
        parsed.CurrentCommandName.Should().BeNull();
        parsed.Prefix.Should().Be("");
    }

    [Fact]
    public void Parse_CursorAtZero_AtStartTrue()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("get-childitem", 0));

        parsed.AtStart.Should().BeTrue();
        parsed.Token.Should().Be("");
        parsed.CurrentCommandName.Should().BeNull();
    }

    [Fact]
    public void Parse_SingleToken_AtStartTrue_NoCommandName()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("get-ch", 6));

        parsed.AtStart.Should().BeTrue();
        parsed.Token.Should().Be("get-ch");
        parsed.CurrentCommandName.Should().BeNull();
    }

    [Fact]
    public void Parse_SecondToken_AtStartFalse_CommandNameSet()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("get-childitem -p", 16));

        parsed.AtStart.Should().BeFalse();
        parsed.Token.Should().Be("-p");
        parsed.CurrentCommandName.Should().Be("get-childitem");
    }

    [Fact]
    public void Parse_CursorAfterWhitespace_HasEmptyToken()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("get-childitem ", 14));

        parsed.AtStart.Should().BeFalse();
        parsed.Token.Should().Be("");
        parsed.CurrentCommandName.Should().Be("get-childitem");
    }

    [Fact]
    public void Parse_ClampsCursorBeyondLength()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("ab", 100));

        parsed.Token.Should().Be("ab");
        parsed.AtStart.Should().BeTrue();
    }

    [Fact]
    public void Parse_NegativeCursor_TreatedAsZero()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("abc", -5));

        parsed.AtStart.Should().BeTrue();
        parsed.Token.Should().Be("");
    }

    [Fact]
    public void Parse_PreservesPrefixBeforeToken()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("cmd arg1 arg", 12));

        parsed.AtStart.Should().BeFalse();
        parsed.CurrentCommandName.Should().Be("cmd");
        parsed.Prefix.Should().Be("cmd arg1 ");
        parsed.Token.Should().Be("arg");
    }

    [Fact]
    public void Parse_MultipleSpacesBetweenTokens_FirstTokenIsCommandName()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("cmd   arg", 9));

        parsed.AtStart.Should().BeFalse();
        parsed.CurrentCommandName.Should().Be("cmd");
        parsed.Token.Should().Be("arg");
    }

    [Fact]
    public void Parse_TokenWithDollarSign_ExtractedCorrectly()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("get-item $HO", 13));

        parsed.AtStart.Should().BeFalse();
        parsed.CurrentCommandName.Should().Be("get-item");
        parsed.Token.Should().Be("$HO");
    }

    [Fact]
    public void Parse_TokenWithPathSeparator_ExtractedCorrectly()
    {
        var parsed = CompletionParser.Parse(new CompletionContext("get-item sub/", 14));

        parsed.AtStart.Should().BeFalse();
        parsed.Token.Should().Be("sub/");
    }
}
