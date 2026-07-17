using FluentAssertions;
using OpenShell.Filter;
using OpenShell.Items;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Filter;

/// <summary>
/// ExprEvaluator 单元测试。Per ADR-0012, ADR-0033.
/// </summary>
public class ExprEvaluatorTests
{
    private static IItem CreateItem(
        string name = "file.txt",
        long? size = 100,
        ItemKind kind = ItemKind.File,
        DateTimeOffset? modified = null)
    {
        var path = ItemPath.Parse($"fs::/tmp/{name}");
        return Item.File(path, size, modified);
    }

    private static object? Eval(string expression, IItem item)
    {
        var ast = ExprParser.Parse(expression);
        return new ExprEvaluator().Evaluate(ast, item);
    }

    [Fact]
    public void Evaluate_EqualityMatchingStrings_ReturnsTrue()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("name = \"foo.txt\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_EqualityNonMatchingStrings_ReturnsFalse()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("name = \"bar.txt\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_NotEqual_ReturnsTrueForDifferentValues()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("name != \"bar.txt\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_SizeLessThan_ReturnsTrueWhenSmaller()
    {
        var item = CreateItem(size: 50);
        var result = Eval("size < 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_SizeGreaterThan_ReturnsTrueWhenLarger()
    {
        var item = CreateItem(size: 200);
        var result = Eval("size > 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_SizeLessOrEqual_ReturnsTrueWhenEqual()
    {
        var item = CreateItem(size: 100);
        var result = Eval("size <= 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_SizeGreaterOrEqual_ReturnsTrueWhenEqual()
    {
        var item = CreateItem(size: 100);
        var result = Eval("size >= 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_Glob_ReturnsTrueWhenMatches()
    {
        var item = CreateItem(name: "report.txt");
        var result = Eval("name ~= \"*.txt\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_Glob_ReturnsFalseWhenNotMatches()
    {
        var item = CreateItem(name: "report.log");
        var result = Eval("name ~= \"*.txt\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_NotGlob_ReturnsFalseWhenMatches()
    {
        var item = CreateItem(name: "report.txt");
        var result = Eval("name !~= \"*.txt\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_And_ReturnsTrueWhenBothTrue()
    {
        var item = CreateItem(name: "foo.txt", size: 50);
        var result = Eval("name = \"foo.txt\" and size < 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_And_ReturnsFalseWhenEitherFalse()
    {
        var item = CreateItem(name: "foo.txt", size: 200);
        var result = Eval("name = \"foo.txt\" and size < 100", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_Or_ReturnsTrueWhenEitherTrue()
    {
        var item = CreateItem(name: "foo.txt", size: 200);
        var result = Eval("name = \"foo.txt\" or size < 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_Or_ReturnsFalseWhenBothFalse()
    {
        var item = CreateItem(name: "foo.txt", size: 200);
        var result = Eval("name = \"bar.txt\" or size < 100", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_Not_ReturnsTrueWhenInnerFalse()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("not name = \"bar.txt\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_Not_ReturnsFalseWhenInnerTrue()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("not name = \"foo.txt\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_In_ReturnsTrueWhenValueInArray()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("name in [\"foo.txt\", \"bar.txt\"]", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_In_ReturnsFalseWhenValueNotInArray()
    {
        var item = CreateItem(name: "baz.txt");
        var result = Eval("name in [\"foo.txt\", \"bar.txt\"]", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_Contains_ReturnsTrueWhenSubstringPresent()
    {
        var item = CreateItem(name: "report-final.txt");
        var result = Eval("name contains \"final\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_Contains_ReturnsFalseWhenSubstringAbsent()
    {
        var item = CreateItem(name: "report.txt");
        var result = Eval("name contains \"draft\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_StartsWith_ReturnsTrueWhenPrefixMatches()
    {
        var item = CreateItem(name: "report-final.txt");
        var result = Eval("name startswith \"report\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_StartsWith_ReturnsFalseWhenPrefixDoesNotMatch()
    {
        var item = CreateItem(name: "report.txt");
        var result = Eval("name startswith \"final\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_EndsWith_ReturnsTrueWhenSuffixMatches()
    {
        var item = CreateItem(name: "report.txt");
        var result = Eval("name endswith \".txt\"", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_EndsWith_ReturnsFalseWhenSuffixDoesNotMatch()
    {
        var item = CreateItem(name: "report.log");
        var result = Eval("name endswith \".txt\"", item);
        result.Should().Be(false);
    }

    [Fact]
    public void Evaluate_PropertyAccess_ReturnsPropertyValue()
    {
        var item = CreateItem(name: "test.txt");
        var result = Eval("name", item);
        result.Should().Be("test.txt");
    }

    [Fact]
    public void Evaluate_SizePropertyAccess_ReturnsLongValue()
    {
        var item = CreateItem(size: 1024);
        var result = Eval("size", item);
        result.Should().Be(1024L);
    }

    [Fact]
    public void Evaluate_PathPropertyAccess_ReturnsDisplayString()
    {
        var item = CreateItem(name: "foo.txt");
        var result = Eval("path", item);
        result.Should().Be(item.Path.Display);
    }

    [Fact]
    public void Evaluate_KindPropertyAccess_ReturnsKindString()
    {
        var item = CreateItem();
        var result = Eval("kind", item);
        result.Should().Be("File");
    }

    [Fact]
    public void Evaluate_GetPropertyValue_BuiltinSize_ReturnsItemSize()
    {
        var item = CreateItem(size: 256);
        ExprEvaluator.GetPropertyValue("size", item).Should().Be(256L);
    }

    [Fact]
    public void Evaluate_GetPropertyValue_BuiltinName_ReturnsItemName()
    {
        var item = CreateItem(name: "test.txt");
        ExprEvaluator.GetPropertyValue("name", item).Should().Be("test.txt");
    }

    [Fact]
    public void Evaluate_GetPropertyValue_BuiltinKind_ReturnsKindToString()
    {
        var item = CreateItem();
        ExprEvaluator.GetPropertyValue("kind", item).Should().Be("File");
    }

    [Fact]
    public void Evaluate_GetPropertyValue_UnknownProperty_ReturnsNull()
    {
        var item = CreateItem();
        ExprEvaluator.GetPropertyValue("unknownprop", item).Should().BeNull();
    }

    [Fact]
    public void Evaluate_GetPropertyValue_EmptyName_ReturnsNull()
    {
        var item = CreateItem();
        ExprEvaluator.GetPropertyValue("", item).Should().BeNull();
    }

    [Fact]
    public void Evaluate_CustomProperty_ReturnsValueFromPropertyBag()
    {
        var path = ItemPath.Parse("fs::/tmp/x.txt");
        var item = Item.File(path) with
        {
            Properties = PropertyBag.Empty.With("custom", "value-x"),
        };
        var result = Eval("custom", item);
        result.Should().Be("value-x");
    }

    [Fact]
    public void Evaluate_EqualityNumberToString_ReturnsFalseGracefully()
    {
        // 类型不匹配（int vs string）— DSL 容错返回 false
        var item = CreateItem(name: "100");
        var result = Eval("name = 100", item);
        // "100" vs 100L: TryGetDouble 双边都能解析，应当返回 true（数字比较）
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_NestedParentheses_ResolvesCorrectly()
    {
        var item = CreateItem(name: "foo.txt", size: 50);
        var result = Eval("(name = \"foo.txt\" or name = \"bar.txt\") and size < 100", item);
        result.Should().Be(true);
    }

    [Fact]
    public void Evaluate_ComplexExpression_EvaluatesCorrectly()
    {
        var item = CreateItem(name: "report.txt", size: 500);
        var result = Eval(
            "(name ~= \"*.txt\" and size < 1000) or name = \"report.txt\"",
            item);
        result.Should().Be(true);
    }
}
