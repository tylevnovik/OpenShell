using FluentAssertions;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// SubExpressionEvaluator 单元测试。Per ADR-0047 §5.
/// 验证 $(...) / @(...) 子表达式求值的输出收集与聚合语义。
/// </summary>
public class SubExpressionEvaluatorTests
{
    // ---- 构造 ----

    [Fact]
    public void Ctor_DoesNotThrow()
    {
        var evaluator = new SubExpressionEvaluator();
        evaluator.Should().NotBeNull();
    }

    // ---- 参数校验 ----

    [Fact]
    public void EvaluateSubExpression_NullExpression_ThrowsArgumentNullException()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();
        var act = () => evaluator.EvaluateSubExpression(null!, vars);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EvaluateSubExpression_NullVariables_ThrowsArgumentNullException()
    {
        var evaluator = new SubExpressionEvaluator();
        var act = () => evaluator.EvaluateSubExpression("1", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EvaluateArraySubExpression_NullExpression_ThrowsArgumentNullException()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();
        var act = () => evaluator.EvaluateArraySubExpression(null!, vars);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EvaluateArraySubExpression_NullVariables_ThrowsArgumentNullException()
    {
        var evaluator = new SubExpressionEvaluator();
        var act = () => evaluator.EvaluateArraySubExpression("1", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- $(...) 单输出 ----

    [Fact]
    public void EvaluateSubExpression_SingleLiteral_ReturnsValue()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("42", vars);

        result.Should().Be(42);
    }

    [Fact]
    public void EvaluateSubExpression_SingleString_ReturnsString()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("\"hello\"", vars);

        result.Should().Be("hello");
    }

    [Fact]
    public void EvaluateSubExpression_SingleVariable_ReturnsValue()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();
        vars.Set("x", 99);

        var result = evaluator.EvaluateSubExpression("$x", vars);

        result.Should().Be(99);
    }

    [Fact]
    public void EvaluateSubExpression_SimpleArithmetic_ReturnsValue()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();
        vars.Set("arr", new[] { 1, 2, 3 });

        // int[] 有 Length 属性 (Count 在 PowerShell 中是别名, Evaluator 当前未实现该别名).
        var result = evaluator.EvaluateSubExpression("$arr.Length + 1", vars);

        result.Should().Be(4);
    }

    // ---- $(...) 多输出 ----

    [Fact]
    public void EvaluateSubExpression_MultipleLiterals_ReturnsArray()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("1; 2; 3", vars);

        result.Should().BeOfType<object[]>();
        var arr = (object[])result!;
        arr.Should().Equal(new object[] { 1, 2, 3 });
    }

    [Fact]
    public void EvaluateSubExpression_CommaSeparatedLiterals_ReturnsArray()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        // 逗号操作符产生数组 (per ADR-0047 §7.1).
        var result = evaluator.EvaluateSubExpression("\"a\", \"b\", \"c\"", vars);

        result.Should().BeAssignableTo<IEnumerable<object>>();
    }

    // ---- $(...) 零输出 ----

    [Fact]
    public void EvaluateSubExpression_EmptyExpression_ReturnsNull()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("", vars);

        result.Should().BeNull();
    }

    [Fact]
    public void EvaluateSubExpression_OnlyAssignment_ReturnsNull()
    {
        // Per ADR-0047 §5.3: 赋值语句不产生输出。
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("$a = 1", vars);

        result.Should().BeNull();
        // 赋值仍应作用于 variables (当前作用域语义)。
        vars.Resolve("a").Should().Be(1);
    }

    [Fact]
    public void EvaluateSubExpression_AssignmentFollowedByExpression_ReturnsExpressionValue()
    {
        // Per ADR-0047 §5.3: 赋值不产生输出, 后续表达式产生输出。
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("$a = 1; $a + 1", vars);

        result.Should().Be(2);
        vars.Resolve("a").Should().Be(1);
    }

    // ---- @(...) 数组语义 ----

    [Fact]
    public void EvaluateArraySubExpression_EmptyExpression_ReturnsEmptyArray()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateArraySubExpression("", vars);

        result.Should().BeEmpty();
        result.Should().BeOfType<object[]>();
    }

    [Fact]
    public void EvaluateArraySubExpression_SingleValue_ReturnsSingleElementArray()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateArraySubExpression("42", vars);

        result.Should().HaveCount(1);
        result[0].Should().Be(42);
    }

    [Fact]
    public void EvaluateArraySubExpression_MultipleValues_ReturnsArray()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateArraySubExpression("1; 2; 3", vars);

        result.Should().Equal(new object[] { 1, 2, 3 });
    }

    [Fact]
    public void EvaluateArraySubExpression_OnlyAssignment_ReturnsEmptyArray()
    {
        // Per ADR-0047 §5.3: 赋值不产生输出 → 空数组。
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateArraySubExpression("$a = 1", vars);

        result.Should().BeEmpty();
        vars.Resolve("a").Should().Be(1);
    }

    // ---- 当前作用域语义 ----

    [Fact]
    public void EvaluateSubExpression_WritesVariablesInCurrentScope()
    {
        // Per ADR-0047 §5.1: $(...) 在当前作用域求值, 不创建新作用域。
        // 赋值的变量应在外部可见。
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        evaluator.EvaluateSubExpression("$tmp = \"hello\"", vars);

        vars.Resolve("tmp").Should().Be("hello");
    }

    // ---- 表达式语句混合 ----

    [Fact]
    public void EvaluateSubExpression_AssignThenUse_ReturnsCorrectValue()
    {
        var evaluator = new SubExpressionEvaluator();
        var vars = new InMemoryVariableRegistry();

        var result = evaluator.EvaluateSubExpression("$x = 10; $y = 20; $x + $y", vars);

        result.Should().Be(30);
        vars.Resolve("x").Should().Be(10);
        vars.Resolve("y").Should().Be(20);
    }
}
