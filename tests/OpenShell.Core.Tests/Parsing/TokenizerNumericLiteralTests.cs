#nullable enable
// ADR-0012 §5: Tokenizer 数字字面量解析测试。
// 覆盖十进制/十六进制/二进制/浮点/指数/类型后缀/数量单位 (KB/MB/GB/TB/PB)。
// 重点验证 1MB 不再被误解析为 "1M" 类型后缀导致 FormatException。

using FluentAssertions;
using OpenShell.Parsing;
using Xunit;

namespace OpenShell.Core.Tests.Parsing;

/// <summary>
/// Tokenizer 数字字面量单测。Per ADR-0012 §5.
/// 验证十进制/十六进制/二进制/浮点/指数/类型后缀/数量单位 (KB/MB/GB/TB/PB)。
/// </summary>
public class TokenizerNumericLiteralTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Tokenizer(source).Tokenize();

    private static Token FirstNumberToken(string source)
    {
        var tokens = Lex(source);
        return tokens.First(t =>
            t.Kind == TokenKind.Integer || t.Kind == TokenKind.Double || t.Kind == TokenKind.Real);
    }

    // ---- 十进制整数 ----

    [Fact]
    public void Integer_Decimal_Parses()
    {
        var t = FirstNumberToken("42");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(42L);
    }

    [Fact]
    public void Integer_Large_Parses()
    {
        var t = FirstNumberToken("9007199254740992");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(9007199254740992L);
    }

    // ---- 十六进制 ----

    [Fact]
    public void Integer_Hex_Parses()
    {
        var t = FirstNumberToken("0xFF");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(255L);
    }

    [Fact]
    public void Integer_HexUppercasePrefix_Parses()
    {
        var t = FirstNumberToken("0X1A");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(26L);
    }

    // ---- 二进制 ----

    [Fact]
    public void Integer_Binary_Parses()
    {
        var t = FirstNumberToken("0b1010");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(10L);
    }

    // ---- 浮点数 ----

    [Fact]
    public void Double_Decimal_Parses()
    {
        var t = FirstNumberToken("3.14");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().BeApproximately(3.14, 0.0001);
    }

    [Fact]
    public void Double_Exponent_Parses()
    {
        var t = FirstNumberToken("1e10");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().BeApproximately(1e10, 0.1);
    }

    [Fact]
    public void Double_NegativeExponent_Parses()
    {
        var t = FirstNumberToken("1e-3");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().BeApproximately(0.001, 0.00001);
    }

    [Fact]
    public void Double_DecimalWithExponent_Parses()
    {
        var t = FirstNumberToken("2.5e3");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().BeApproximately(2500.0, 0.01);
    }

    // ---- 类型后缀 ----

    [Fact]
    public void Integer_LongSuffix_Parses()
    {
        var t = FirstNumberToken("1l");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1L);
    }

    [Fact]
    public void Double_DecimalSuffix_Parses()
    {
        // 'd' 后缀: PowerShell decimal, 解析为 double.
        var t = FirstNumberToken("3.14d");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().BeApproximately(3.14, 0.0001);
    }

    [Fact]
    public void Integer_DecimalSuffix_NoDecimalPoint_ParsesAsDouble()
    {
        // 1d (无小数点): 'd' 后缀强制解析为 double.
        var t = FirstNumberToken("1d");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().Be(1.0);
    }

    // ---- 数量单位 (ADR-0012 §5) ----

    [Fact]
    public void Unit_KB_Parses()
    {
        var t = FirstNumberToken("1KB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1024L);
    }

    [Fact]
    public void Unit_MB_Parses()
    {
        // 1MB 曾因 'm' 类型后缀冲突抛 FormatException (回归测试).
        var t = FirstNumberToken("1MB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1048576L);
    }

    [Fact]
    public void Unit_GB_Parses()
    {
        var t = FirstNumberToken("1GB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1073741824L);
    }

    [Fact]
    public void Unit_TB_Parses()
    {
        var t = FirstNumberToken("1TB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1099511627776L);
    }

    [Fact]
    public void Unit_PB_Parses()
    {
        var t = FirstNumberToken("1PB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1125899906842624L);
    }

    [Fact]
    public void Unit_LargerValue_Parses()
    {
        var t = FirstNumberToken("100KB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(102400L);
    }

    [Fact]
    public void Unit_2MB_Parses()
    {
        var t = FirstNumberToken("2MB");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(2L * 1024 * 1024);
    }

    [Fact]
    public void Unit_DoubleWithMB_Parses()
    {
        var t = FirstNumberToken("1.5MB");
        t.Kind.Should().Be(TokenKind.Double);
        ((double)t.Value!).Should().Be(1.5 * 1024 * 1024);
    }

    [Fact]
    public void Unit_CaseInsensitive_Parses()
    {
        // 小写单位也应被接受.
        var t = FirstNumberToken("1kb");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().Be(1024L);
    }

    // ---- 单位无 B 后缀: 不识别为单位, 数字单独成 token ----

    [Fact]
    public void Unit_WithoutBSuffix_NotMatchedAsUnit()
    {
        // 1K (无 B) 不应被识别为 KB 单位; 'K' 应为独立标识符.
        var tokens = Lex("1K");
        tokens.Should().Contain(t => t.Kind == TokenKind.Integer && (long)t.Value! == 1L);
        tokens.Should().Contain(t => t.Kind == TokenKind.Identifier && t.Text == "K");
    }

    [Fact]
    public void Unit_MAlone_NotMatchedAsUnit()
    {
        // 1M (无 B): 'M' 应为独立标识符, 不是类型后缀 (修复 'm' 冲突后的回归测试).
        var tokens = Lex("1M");
        tokens.Should().Contain(t => t.Kind == TokenKind.Integer && (long)t.Value! == 1L);
        tokens.Should().Contain(t => t.Kind == TokenKind.Identifier && t.Text == "M");
    }

    // ---- 管道上下文中的数字单位 (ADR-0012 §1) ----

    [Fact]
    public void Unit_MB_InPipeline_DoesNotConflictWithWhereAlias()
    {
        // 集成验证: 1MB 在 where 管道中不与 where/select 别名冲突.
        var tokens = Lex("where { $_.Size -gt 1MB }");
        tokens.Should().Contain(t => t.Kind == TokenKind.Integer && (long)t.Value! == 1048576L);
    }

    // ---- 组合后缀 (T-110, 借鉴 PS tokenizer.cs:4025-4083) ----
    // 验证数字字面量后缀将 token.Value 转换为对应 .NET 类型。

    [Fact]
    public void Suffix_Byte_CombinedUy_Parses()
    {
        // 0xFFuy → byte 255
        var t = FirstNumberToken("0xFFuy");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<byte>();
        t.Value.Should().Be((byte)255);
    }

    [Fact]
    public void Suffix_Byte_CombinedUY_Uppercase_Parses()
    {
        // 大写后缀 UY 亦应识别 (PS 后缀不区分大小写)。
        var t = FirstNumberToken("10UY");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<byte>();
        t.Value.Should().Be((byte)10);
    }

    [Fact]
    public void Suffix_ULong_CombinedUl_Parses()
    {
        var t = FirstNumberToken("1ul");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<ulong>();
        t.Value.Should().Be(1UL);
    }

    [Fact]
    public void Suffix_ULong_CombinedLu_Parses()
    {
        var t = FirstNumberToken("1lu");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<ulong>();
        t.Value.Should().Be(1UL);
    }

    [Fact]
    public void Suffix_UShort_CombinedUs_Parses()
    {
        var t = FirstNumberToken("1us");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<ushort>();
        t.Value.Should().Be((ushort)1);
    }

    [Fact]
    public void Suffix_UShort_CombinedSu_Parses()
    {
        var t = FirstNumberToken("1su");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<ushort>();
        t.Value.Should().Be((ushort)1);
    }

    [Fact]
    public void Suffix_UInt_Single_Parses()
    {
        var t = FirstNumberToken("42u");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<uint>();
        t.Value.Should().Be(42u);
    }

    [Fact]
    public void Suffix_SByte_Single_Parses()
    {
        var t = FirstNumberToken("1y");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<sbyte>();
        t.Value.Should().Be((sbyte)1);
    }

    [Fact]
    public void Suffix_Short_Single_Parses()
    {
        var t = FirstNumberToken("1s");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<short>();
        t.Value.Should().Be((short)1);
    }

    [Fact]
    public void Suffix_Long_Single_Parses_AsLong()
    {
        // 已有 Integer_LongSuffix_Parses 验证 1l == 1L; 此处补充类型断言。
        var t = FirstNumberToken("1l");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<long>();
        t.Value.Should().Be(1L);
    }

    [Fact]
    public void Suffix_Hex_WithByteSuffix_Parses()
    {
        // 0x10uy → byte 16
        var t = FirstNumberToken("0x10uy");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<byte>();
        t.Value.Should().Be((byte)16);
    }

    [Fact]
    public void Suffix_Binary_WithULong_Parses()
    {
        // 0b1010ul → ulong 10
        var t = FirstNumberToken("0b1010ul");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<ulong>();
        t.Value.Should().Be(10UL);
    }

    [Fact]
    public void Suffix_NoSuffix_DefaultIsLong()
    {
        // 无后缀默认 long (向后兼容).
        var t = FirstNumberToken("42");
        t.Kind.Should().Be(TokenKind.Integer);
        t.Value.Should().BeOfType<long>();
        t.Value.Should().Be(42L);
    }
}
