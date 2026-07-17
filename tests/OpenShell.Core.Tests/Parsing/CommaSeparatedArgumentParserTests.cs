#nullable enable
// ADR-0012 §1/§6: 逗号分隔参数解析测试。
// 验证 Select-Object -Property Name, Size 等形式的逗号分隔值解析为 ArrayExpression。

using FluentAssertions;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using Xunit;

namespace OpenShell.Core.Tests.Parsing;

/// <summary>
/// PowerShellParser 逗号分隔命令参数测试。Per ADR-0012 §1/§6.
/// PowerShell 语义：命令参数中的逗号表示数组元素分隔符（单参数多值），
/// 而非参数分隔符。`Select-Object -Property Name, Size` 应解析为
/// NamedArgument("Property", ArrayExpression([Name, Size]))。
/// </summary>
public class CommaSeparatedArgumentParserTests
{
    private static ScriptBlockAst Parse(string source) => PowerShellParser.Parse(source);

    // ---- 命名参数 + 逗号分隔值 ----

    [Fact]
    public void NamedArgument_CommaSeparatedValues_ParsedAsArray()
    {
        var ast = Parse("Select-Object -Property Name, Size");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        cmd.Name.Should().Be("Select-Object");
        cmd.Arguments.Should().HaveCount(1);
        cmd.Arguments[0].Should().BeOfType<NamedArgument>()
            .Which.Name.Should().Be("Property");
        var namedArg = (NamedArgument)cmd.Arguments[0];
        namedArg.Value.Should().BeOfType<ArrayExpression>()
            .Which.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void NamedArgument_ThreeCommaSeparatedValues_ParsedAsArray()
    {
        var ast = Parse("Select-Object -Property Name, Size, Modified");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        var namedArg = cmd.Arguments[0].Should().BeOfType<NamedArgument>().Subject;
        namedArg.Value.Should().BeOfType<ArrayExpression>()
            .Which.Elements.Should().HaveCount(3);
    }

    [Fact]
    public void NamedArgument_SingleValue_NotArray()
    {
        // 单值不应包装为数组。
        var ast = Parse("Select-Object -Property Name");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        var namedArg = cmd.Arguments[0].Should().BeOfType<NamedArgument>().Subject;
        namedArg.Value.Should().NotBeOfType<ArrayExpression>();
    }

    // ---- 位置参数 + 逗号分隔值 ----

    [Fact]
    public void PositionalArgument_CommaSeparatedValues_ParsedAsSingleArrayArg()
    {
        // PowerShell 语义：foo a, b, c 传递单个数组参数 [a, b, c]
        var ast = Parse("foo a, b, c");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        cmd.Arguments.Should().HaveCount(1);
        cmd.Arguments[0].Should().BeOfType<PositionalArgument>()
            .Which.Value.Should().BeOfType<ArrayExpression>()
            .Which.Elements.Should().HaveCount(3);
    }

    [Fact]
    public void PositionalArgument_SpaceSeparated_ThreeSeparateArgs()
    {
        // 空格分隔是三个独立参数（非数组）
        var ast = Parse("foo a b c");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        cmd.Arguments.Should().HaveCount(3);
        cmd.Arguments.Should().AllBeOfType<PositionalArgument>();
    }

    [Fact]
    public void MixedCommaAndSpace_ParsedCorrectly()
    {
        // foo a, b c → 两个参数：[a,b] 数组 + c 位置参数
        var ast = Parse("foo a, b c");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        cmd.Arguments.Should().HaveCount(2);
        cmd.Arguments[0].Should().BeOfType<PositionalArgument>()
            .Which.Value.Should().BeOfType<ArrayExpression>()
            .Which.Elements.Should().HaveCount(2);
        cmd.Arguments[1].Should().BeOfType<PositionalArgument>();
    }

    // ---- 逗号分隔的数字/字符串 ----

    [Fact]
    public void CommaSeparatedNumbers_ParsedAsArray()
    {
        var ast = Parse("foo 1, 2, 3");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        cmd.Arguments.Should().HaveCount(1);
        var arr = cmd.Arguments[0].Should().BeOfType<PositionalArgument>()
            .Subject.Value.Should().BeOfType<ArrayExpression>().Subject;
        arr.Elements.Should().HaveCount(3);
    }

    [Fact]
    public void CommaSeparatedStrings_ParsedAsArray()
    {
        var ast = Parse("foo 'a', 'b'");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var cmd = stmt.Pipeline.Commands[0];
        cmd.Arguments.Should().HaveCount(1);
        cmd.Arguments[0].Should().BeOfType<PositionalArgument>()
            .Which.Value.Should().BeOfType<ArrayExpression>()
            .Which.Elements.Should().HaveCount(2);
    }

    // ---- 管道中的逗号分隔参数 ----

    [Fact]
    public void CommaSeparatedInPipeline_ParsedCorrectly()
    {
        var ast = Parse("get-childitem | select name, size");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        stmt.Pipeline.Commands.Should().HaveCount(2);
        var select = stmt.Pipeline.Commands[1];
        select.Name.Should().Be("select");
        select.Arguments.Should().HaveCount(1);
        select.Arguments[0].Should().BeOfType<PositionalArgument>()
            .Which.Value.Should().BeOfType<ArrayExpression>()
            .Which.Elements.Should().HaveCount(2);
    }

    [Fact]
    public void WhereAliasInPipeline_ParsesAsCommand()
    {
        // ADR-0012: `where` 是 Where-Object 的命令别名，不是保留关键字。
        // 使用 1MB 验证 Tokenizer 数字单位解析 (ADR-0012 §5) 不与 where 别名冲突。
        var ast = Parse("get-childitem | where { $_.Size -gt 1MB }");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        stmt.Pipeline.Commands.Should().HaveCount(2);
        var where = stmt.Pipeline.Commands[1];
        where.Name.Should().Be("where");
        where.Arguments.Should().HaveCount(1);
        where.Arguments[0].Should().BeOfType<ScriptBlockArgument>();
    }

    [Fact]
    public void ForEachAliasInPipeline_ParsesAsCommand()
    {
        // ADR-0012 §7: `foreach` 在管道上下文中作为 ForEach-Object 命令别名。
        var ast = Parse("1..10 | foreach { $_ * 2 }");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        stmt.Pipeline.Commands.Should().HaveCount(2);
        var foreachCmd = stmt.Pipeline.Commands[1];
        foreachCmd.Name.Should().Be("foreach");
        foreachCmd.Arguments.Should().HaveCount(1);
        foreachCmd.Arguments[0].Should().BeOfType<ScriptBlockArgument>();
    }

    [Fact]
    public void ForEachAliasAtStatementStart_ParsesAsCommand()
    {
        // PowerShell 语义：`foreach { }` 在语句起始位置且无 `(` 后跟时视为 ForEach-Object 命令。
        var ast = Parse("foreach { $_ * 2 }");
        var stmt = ast.Statements[0].Should().BeOfType<PipelineStatement>().Subject;
        var foreachCmd = stmt.Pipeline.Commands[0];
        foreachCmd.Name.Should().Be("foreach");
        foreachCmd.Arguments.Should().HaveCount(1);
        foreachCmd.Arguments[0].Should().BeOfType<ScriptBlockArgument>();
    }

    // ---- foreach 语句仍正常工作 ----

    [Fact]
    public void ForEachStatement_StandardForm_ParsesCorrectly()
    {
        var ast = Parse("foreach ($x in 1..10) { $x }");
        var stmt = ast.Statements[0].Should().BeOfType<ForEachStatement>().Subject;
        stmt.Kind.Should().Be(ForEachKind.Item);
        stmt.Variable.Should().Be("x");
        stmt.Iterable.Should().NotBeNull();
    }
}
