#nullable enable
// SourceSpan / SourcePosition 单元测试。Per T-101（借鉴 PS IScriptExtent）。
// 验证 Offset 体系填充正确 + 便捷属性（StartOffset/EndOffset/Length/StartLine 等）。

using FluentAssertions;
using OpenShell.Parsing;
using Xunit;
using System.Linq;

namespace OpenShell.Core.Tests.Parsing;

/// <summary>
/// SourceSpan / SourcePosition 单测。Per T-101。
/// 验证 Tokenizer 填充 Offset 正确 + SourceSpan 便捷属性（借鉴 PS IScriptExtent）。
/// </summary>
public class SourceSpanTests
{
    // =========================================================================
    // SourcePosition
    // =========================================================================

    [Fact]
    public void SourcePosition_Zero_HasOffsetZero()
    {
        var z = SourcePosition.Zero;
        z.Line.Should().Be(1);
        z.Column.Should().Be(1);
        z.Offset.Should().Be(0);
    }

    [Fact]
    public void SourcePosition_ToString_FormatsLineColumn()
    {
        var p = new SourcePosition(3, 5, 20);
        p.ToString().Should().Be("(3,5)");
    }

    // =========================================================================
    // SourceSpan 便捷属性（借鉴 PS IScriptExtent）
    // =========================================================================

    [Fact]
    public void SourceSpan_StartOffset_EndOffset_Length()
    {
        var start = new SourcePosition(1, 1, 0);
        var end = new SourcePosition(1, 4, 3);
        var span = new SourceSpan(start, end);

        span.StartOffset.Should().Be(0);
        span.EndOffset.Should().Be(3);
        span.Length.Should().Be(3);
    }

    [Fact]
    public void SourceSpan_StartLine_StartColumn_EndLine_EndColumn()
    {
        var start = new SourcePosition(2, 3, 10);
        var end = new SourcePosition(2, 8, 15);
        var span = new SourceSpan(start, end);

        span.StartLine.Should().Be(2);
        span.StartColumn.Should().Be(3);
        span.EndLine.Should().Be(2);
        span.EndColumn.Should().Be(8);
    }

    [Fact]
    public void SourceSpan_Empty_HasZeroOffset()
    {
        var e = SourceSpan.Empty;
        e.StartOffset.Should().Be(0);
        e.EndOffset.Should().Be(0);
        e.Length.Should().Be(0);
    }

    [Fact]
    public void SourceSpan_MultiLine_Length()
    {
        // 跨行 span：offset 10 → 30，长度 20。
        var start = new SourcePosition(1, 5, 10);
        var end = new SourcePosition(3, 2, 30);
        var span = new SourceSpan(start, end);
        span.Length.Should().Be(20);
    }

    // =========================================================================
    // Tokenizer 填充 Offset 验证
    // =========================================================================

    [Fact]
    public void Tokenizer_FillsOffset_ForInteger()
    {
        var tokens = new Tokenizer("123").Tokenize();
        var tok = tokens[0];
        tok.Span.StartOffset.Should().Be(0);
        tok.Span.EndOffset.Should().Be(3);
        tok.Span.Length.Should().Be(3);
    }

    [Fact]
    public void Tokenizer_FillsOffset_ForSecondToken()
    {
        // "12 34" —— 12 在 offset 0-2，34 在 offset 3-5。
        var tokens = new Tokenizer("12 34").Tokenize();
        var first = tokens[0];
        var second = tokens[1];

        first.Span.StartOffset.Should().Be(0);
        first.Span.EndOffset.Should().Be(2);

        second.Span.StartOffset.Should().Be(3);
        second.Span.EndOffset.Should().Be(5);
    }

    [Fact]
    public void Tokenizer_Offset_AfterNewline()
    {
        // "12\n34" —— 12 在 offset 0-2，换行 token offset 2-3，34 在 offset 3-5。
        var tokens = new Tokenizer("12\n34").Tokenize();
        // 过滤掉 NewLine token，取数字 token。
        var nums = tokens.Where(t => t.Kind == TokenKind.Integer).ToList();
        var first = nums[0];
        var second = nums[1];

        first.Span.StartOffset.Should().Be(0);
        second.Span.StartOffset.Should().Be(3);
        second.Span.Start.Line.Should().Be(2);
        second.Span.Start.Column.Should().Be(1);
    }

    [Fact]
    public void Tokenizer_StringLiteral_Offset()
    {
        var tokens = new Tokenizer("\"hello\"").Tokenize();
        var tok = tokens[0];
        tok.Span.StartOffset.Should().Be(0);
        tok.Span.EndOffset.Should().Be(7); // 含引号
    }

    [Fact]
    public void Tokenizer_HereString_Offset()
    {
        // @"<newline>body<newline>"@ —— 10 字符：@ " \n b o d y \n " @
        var tokens = new Tokenizer("@\"\nbody\n\"@").Tokenize();
        var tok = tokens[0];
        tok.Span.StartOffset.Should().Be(0);
        tok.Span.EndOffset.Should().Be(10);
    }

    // =========================================================================
    // T-108: here-string false-footer 检测（借鉴 PS tokenizer.cs:2637-2683）
    // =========================================================================

    [Fact]
    public void HereString_FalseFooter_IndentedClose_IsBody()
    {
        // 行首带空白的 "@ 应视为 body 而非闭合标记（PS false-footer 语义）。
        // @"<newline>body<newline>  "@<newline>realclose"@
        // 第一行 "  "@ 因缩进不是闭合，继续到下一行 "@ 闭合。
        var tokens = new Tokenizer("@\"\nbody\n  \"@\n\"@").Tokenize();
        var tok = tokens[0];
        tok.Kind.Should().Be(TokenKind.HereString);
        // body 应包含 "body\n  \"@\n"（缩进的 "@ 当作 body）。
        tok.Value?.ToString().Should().Contain("body");
        tok.Value?.ToString().Should().Contain("\"@");
    }

    [Fact]
    public void HereString_Unclosed_ConsumesToEnd()
    {
        // 未闭合 here-string（到 EOF 无行首 "@）——优雅处理，消费到末尾。
        var tokens = new Tokenizer("@\"\nbody").Tokenize();
        var tok = tokens[0];
        tok.Kind.Should().Be(TokenKind.HereString);
        tok.Value?.ToString().Should().Be("body");
    }

    [Fact]
    public void HereString_CloseMustBeAtLineStart()
    {
        // "@ 必须在行首（_column==1）。
        // @"<newline>x"@<newline> —— x 后的 "@ 不在行首，应视为 body。
        var tokens = new Tokenizer("@\"\nx\"@\n\"@").Tokenize();
        var tok = tokens[0];
        tok.Kind.Should().Be(TokenKind.HereString);
        // body 应为 x"@（因为 "@ 不在行首）。
        tok.Value?.ToString().Should().Contain("x\"@");
    }
}
