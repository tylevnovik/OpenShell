using FluentAssertions;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// ArrayLiteral 单元测试。Per ADR-0047 §7.
/// </summary>
public class ArrayLiteralTests
{
    [Fact]
    public void Create_FiltersNullElements()
    {
        var arr = ArrayLiteral.Create("a", null, "b", null, "c");
        arr.Should().Equal(new object[] { "a", "b", "c" });
    }

    [Fact]
    public void Create_AllNonNull_PreservesAll()
    {
        var arr = ArrayLiteral.Create(1, "two", 3.0);
        arr.Should().HaveCount(3);
        arr[0].Should().Be(1);
        arr[1].Should().Be("two");
        arr[2].Should().Be(3.0);
    }

    [Fact]
    public void Create_AllNull_ReturnsEmptyArray()
    {
        var arr = ArrayLiteral.Create(null, null, null);
        arr.Should().BeEmpty();
    }

    [Fact]
    public void Create_NoArgs_ReturnsEmptyArray()
    {
        var arr = ArrayLiteral.Create();
        arr.Should().BeEmpty();
        arr.Should().BeOfType<object[]>();
    }

    [Fact]
    public void CreateRange_FiltersNullElements()
    {
        var arr = ArrayLiteral.CreateRange(new object?[] { 1, null, 2, null, 3 });
        arr.Should().Equal(new object[] { 1, 2, 3 });
    }

    [Fact]
    public void CreateRange_NullArg_ThrowsArgumentNullException()
    {
        var act = () => ArrayLiteral.CreateRange(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateRange_EmptySource_ReturnsEmptyArray()
    {
        var arr = ArrayLiteral.CreateRange(Array.Empty<object?>());
        arr.Should().BeEmpty();
    }

    [Fact]
    public void Empty_ReturnsEmptyObjectArray()
    {
        var arr = ArrayLiteral.Empty();
        arr.Should().BeEmpty();
        arr.Should().BeOfType<object[]>();
    }

    [Fact]
    public void Create_PreservesElementType()
    {
        var arr = ArrayLiteral.Create(1, 2, 3);
        arr.Should().BeOfType<object[]>();
        arr[0].Should().Be(1);
        arr[0].Should().BeOfType<int>();
    }

    [Fact]
    public void Create_MixedTypes_WrappedInObjectArray()
    {
        var arr = ArrayLiteral.Create("str", 42, true);
        arr.Should().BeOfType<object[]>();
        arr.Length.Should().Be(3);
    }
}
