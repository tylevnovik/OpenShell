// CharTraits 单元测试。验证 PS 借鉴的字符分类表正确性（T-100）。
using FluentAssertions;
using OpenShell.Parsing;
using Xunit;

namespace OpenShell.Core.Tests.Parsing;

public class CharTraitsTests
{
    // =========================================================================
    // SpecialChars 常量验证
    // =========================================================================

    [Fact]
    public void SpecialChars_NoBreakSpace_Is_0x00a0()
    {
        SpecialChars.NoBreakSpace.Should().Be((char)0x00a0);
    }

    [Fact]
    public void SpecialChars_EnDash_Is_0x2013()
    {
        SpecialChars.EnDash.Should().Be((char)0x2013);
    }

    // =========================================================================
    // IsWhitespace
    // =========================================================================

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    public void IsWhitespace_CommonWhitespace_Returns_True(char c)
    {
        c.IsWhitespace().Should().BeTrue();
    }

    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    public void IsWhitespace_Newline_Returns_False(char c)
    {
        // 换行不算空白（per PS 设计）
        c.IsWhitespace().Should().BeFalse();
    }

    [Fact]
    public void IsWhitespace_NoBreakSpace_Returns_True()
    {
        SpecialChars.NoBreakSpace.IsWhitespace().Should().BeTrue();
    }

    [Fact]
    public void IsWhitespace_Letter_Returns_False()
    {
        'a'.IsWhitespace().Should().BeFalse();
    }

    // =========================================================================
    // IsDash
    // =========================================================================

    [Theory]
    [InlineData('-')]
    public void IsDash_Hyphen_Returns_True(char c) => c.IsDash().Should().BeTrue();

    [Fact]
    public void IsDash_SpecialDashes_Returns_True()
    {
        SpecialChars.EnDash.IsDash().Should().BeTrue();
        SpecialChars.EmDash.IsDash().Should().BeTrue();
        SpecialChars.HorizontalBar.IsDash().Should().BeTrue();
    }

    [Fact]
    public void IsDash_Letter_Returns_False() => 'a'.IsDash().Should().BeFalse();

    // =========================================================================
    // IsSingleQuote / IsDoubleQuote
    // =========================================================================

    [Fact]
    public void IsSingleQuote_NormalAndSpecial_Returns_True()
    {
        '\''.IsSingleQuote().Should().BeTrue();
        SpecialChars.QuoteSingleLeft.IsSingleQuote().Should().BeTrue();
        SpecialChars.QuoteSingleRight.IsSingleQuote().Should().BeTrue();
    }

    [Fact]
    public void IsDoubleQuote_NormalAndSpecial_Returns_True()
    {
        '"'.IsDoubleQuote().Should().BeTrue();
        SpecialChars.QuoteDoubleLeft.IsDoubleQuote().Should().BeTrue();
        SpecialChars.QuoteDoubleRight.IsDoubleQuote().Should().BeTrue();
    }

    // =========================================================================
    // IsVariableStart
    // =========================================================================

    [Theory]
    [InlineData('$')]
    [InlineData('?')]
    [InlineData(':')]
    [InlineData('_')]
    [InlineData('a')]
    [InlineData('A')]
    [InlineData('0')]
    public void IsVariableStart_ValidChars_Returns_True(char c) => c.IsVariableStart().Should().BeTrue();

    [Theory]
    [InlineData('!')]
    [InlineData('@')]
    [InlineData('-')]
    public void IsVariableStart_InvalidChars_Returns_False(char c) => c.IsVariableStart().Should().BeFalse();

    // =========================================================================
    // IsIdentifierStart / IsIdentifierFollow
    // =========================================================================

    [Theory]
    [InlineData('a')]
    [InlineData('A')]
    [InlineData('z')]
    [InlineData('Z')]
    [InlineData('_')]
    public void IsIdentifierStart_ValidChars_Returns_True(char c) => c.IsIdentifierStart().Should().BeTrue();

    [Theory]
    [InlineData('0')]
    [InlineData('-')]
    [InlineData('$')]
    [InlineData('!')]
    public void IsIdentifierStart_InvalidChars_Returns_False(char c) => c.IsIdentifierStart().Should().BeFalse();

    [Theory]
    [InlineData('a')]
    [InlineData('A')]
    [InlineData('0')]
    [InlineData('9')]
    [InlineData('_')]
    public void IsIdentifierFollow_ValidChars_Returns_True(char c) => c.IsIdentifierFollow().Should().BeTrue();

    [Theory]
    [InlineData('-')]
    [InlineData('$')]
    [InlineData('!')]
    public void IsIdentifierFollow_InvalidChars_Returns_False(char c) => c.IsIdentifierFollow().Should().BeFalse();

    // =========================================================================
    // IsHexDigit / IsDecimalDigit / IsBinaryDigit
    // =========================================================================

    [Theory]
    [InlineData('0')]
    [InlineData('9')]
    [InlineData('a')]
    [InlineData('f')]
    [InlineData('A')]
    [InlineData('F')]
    public void IsHexDigit_ValidChars_Returns_True(char c) => c.IsHexDigit().Should().BeTrue();

    [Theory]
    [InlineData('g')]
    [InlineData('z')]
    [InlineData('G')]
    [InlineData('-')]
    public void IsHexDigit_InvalidChars_Returns_False(char c) => c.IsHexDigit().Should().BeFalse();

    [Theory]
    [InlineData('0')]
    [InlineData('9')]
    public void IsDecimalDigit_ValidChars_Returns_True(char c) => c.IsDecimalDigit().Should().BeTrue();

    [Theory]
    [InlineData('a')]
    [InlineData('/')]
    public void IsDecimalDigit_InvalidChars_Returns_False(char c) => c.IsDecimalDigit().Should().BeFalse();

    [Theory]
    [InlineData('0')]
    [InlineData('1')]
    public void IsBinaryDigit_ValidChars_Returns_True(char c) => c.IsBinaryDigit().Should().BeTrue();

    [Theory]
    [InlineData('2')]
    [InlineData('a')]
    public void IsBinaryDigit_InvalidChars_Returns_False(char c) => c.IsBinaryDigit().Should().BeFalse();

    // =========================================================================
    // IsTypeSuffix / IsMultiplierStart
    // =========================================================================

    [Theory]
    [InlineData('d')]
    [InlineData('l')]
    [InlineData('n')]
    [InlineData('s')]
    [InlineData('u')]
    [InlineData('y')]
    [InlineData('D')]
    [InlineData('L')]
    public void IsTypeSuffix_ValidChars_Returns_True(char c) => c.IsTypeSuffix().Should().BeTrue();

    [Theory]
    [InlineData('a')]
    [InlineData('z')]
    public void IsTypeSuffix_InvalidChars_Returns_False(char c) => c.IsTypeSuffix().Should().BeFalse();

    [Theory]
    [InlineData('g')]
    [InlineData('k')]
    [InlineData('m')]
    [InlineData('p')]
    [InlineData('t')]
    public void IsMultiplierStart_ValidChars_Returns_True(char c) => c.IsMultiplierStart().Should().BeTrue();

    [Theory]
    [InlineData('a')]
    [InlineData('d')]
    public void IsMultiplierStart_InvalidChars_Returns_False(char c) => c.IsMultiplierStart().Should().BeFalse();

    // =========================================================================
    // ForceStartNewToken
    // =========================================================================

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData('|')]
    [InlineData(';')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData(',')]
    [InlineData('&')]
    public void ForceStartNewToken_TokenBreakingChars_Returns_True(char c) => c.ForceStartNewToken().Should().BeTrue();

    [Theory]
    [InlineData('a')]
    [InlineData('"')]
    [InlineData('\'')]
    [InlineData('@')]
    public void ForceStartNewToken_NonBreakingChars_Returns_False(char c) => c.ForceStartNewToken().Should().BeFalse();

    // =========================================================================
    // IsCurlyBracket
    // =========================================================================

    [Theory]
    [InlineData('{')]
    [InlineData('}')]
    public void IsCurlyBracket_Brackets_Returns_True(char c) => CharExtensions.IsCurlyBracket(c).Should().BeTrue();

    [Theory]
    [InlineData('a')]
    [InlineData('[')]
    public void IsCurlyBracket_NonBrackets_Returns_False(char c) => CharExtensions.IsCurlyBracket(c).Should().BeFalse();
}
