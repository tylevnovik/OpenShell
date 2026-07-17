using System.Collections;
using System.Globalization;
using FluentAssertions;
using OpenShell.Runtime;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// TypeCoercer 单元测试。Per ADR-0047 §3.
/// </summary>
public class TypeCoercerTests
{
    // ---- Coerce: numeric ----

    [Fact]
    public void Coerce_StringToInt_ValidNumber_ReturnsInt()
    {
        TypeCoercer.Coerce("42", typeof(int)).Should().Be(42);
        TypeCoercer.Coerce("-7", typeof(int)).Should().Be(-7);
    }

    [Fact]
    public void Coerce_StringToInt_InvalidFormat_ThrowsInvalidCastException()
    {
        var act = () => TypeCoercer.Coerce("abc", typeof(int));
        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Coerce_DoubleToInt_TruncatesTowardZero()
    {
        TypeCoercer.Coerce(3.7, typeof(int)).Should().Be(3);
        TypeCoercer.Coerce(-3.7, typeof(int)).Should().Be(-3);
    }

    [Fact]
    public void Coerce_BoolToInt_ReturnsOneOrZero()
    {
        TypeCoercer.Coerce(true, typeof(int)).Should().Be(1);
        TypeCoercer.Coerce(false, typeof(int)).Should().Be(0);
    }

    [Fact]
    public void Coerce_CharToInt_ReturnsUnicodeCodePoint()
    {
        TypeCoercer.Coerce('A', typeof(int)).Should().Be(65);
    }

    [Fact]
    public void Coerce_LongToInt_Truncates()
    {
        // 1234567890123L = 0x11F71FB04CB; lower 32 bits = 0x71FB04CB = 1912276171.
        TypeCoercer.Coerce(1234567890123L, typeof(int)).Should().Be(1912276171); // wraps (lower 32 bits)
    }

    [Fact]
    public void Coerce_NullToInt_ThrowsInvalidCastException()
    {
        var act = () => TypeCoercer.Coerce(null, typeof(int));
        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Coerce_StringToLong_ValidNumber_ReturnsLong()
    {
        TypeCoercer.Coerce("9223372036854775807", typeof(long)).Should().Be(long.MaxValue);
    }

    [Fact]
    public void Coerce_StringToDouble_UsesInvariantCulture()
    {
        // Force a culture that uses comma as decimal separator — must NOT break parse.
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            TypeCoercer.Coerce("3.14", typeof(double)).Should().Be(3.14);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Coerce_IntToDouble_Promotes()
    {
        TypeCoercer.Coerce(42, typeof(double)).Should().Be(42.0);
    }

    [Fact]
    public void Coerce_StringToDecimal_ParsesInvariant()
    {
        TypeCoercer.Coerce("3.14", typeof(decimal)).Should().Be(3.14m);
    }

    // ---- Coerce: bool ----

    [Fact]
    public void Coerce_StringToBool_TrueFalseCaseInsensitive()
    {
        TypeCoercer.Coerce("true", typeof(bool)).Should().Be(true);
        TypeCoercer.Coerce("TRUE", typeof(bool)).Should().Be(true);
        TypeCoercer.Coerce("False", typeof(bool)).Should().Be(false);
    }

    [Fact]
    public void Coerce_IntToBool_NonZeroIsTrue()
    {
        TypeCoercer.Coerce(0, typeof(bool)).Should().Be(false);
        TypeCoercer.Coerce(1, typeof(bool)).Should().Be(true);
        TypeCoercer.Coerce(-1, typeof(bool)).Should().Be(true);
    }

    [Fact]
    public void Coerce_DoubleToBool_NonZeroIsTrue()
    {
        TypeCoercer.Coerce(0.0, typeof(bool)).Should().Be(false);
        TypeCoercer.Coerce(0.001, typeof(bool)).Should().Be(true);
    }

    [Fact]
    public void Coerce_NullToBool_ReturnsFalse()
    {
        // Per ADR: null → bool = false.
        TypeCoercer.Coerce(null, typeof(bool)).Should().Be(false);
    }

    [Fact]
    public void Coerce_ReferenceTypeToBool_ReturnsTrue()
    {
        // Per ADR-0047 §3.1: non-null reference type → true. Strings have their own
        // bool-parsing rule, so use a non-string reference type to exercise the fallback.
        TypeCoercer.Coerce(new object(), typeof(bool)).Should().Be(true);
    }

    [Fact]
    public void Coerce_StringToBool_NumericString_ParsesAsNumber()
    {
        TypeCoercer.Coerce("0", typeof(bool)).Should().Be(false);
        TypeCoercer.Coerce("1", typeof(bool)).Should().Be(true);
        TypeCoercer.Coerce("3.5", typeof(bool)).Should().Be(true);
    }

    // ---- Coerce: string ----

    [Fact]
    public void Coerce_IntToString_UsesToString()
    {
        TypeCoercer.Coerce(42, typeof(string)).Should().Be("42");
    }

    [Fact]
    public void Coerce_NullToString_ReturnsEmptyString()
    {
        TypeCoercer.Coerce(null, typeof(string)).Should().Be("");
    }

    [Fact]
    public void Coerce_BoolToString_ReturnsTrueFalse()
    {
        TypeCoercer.Coerce(true, typeof(string)).Should().Be("True");
        TypeCoercer.Coerce(false, typeof(string)).Should().Be("False");
    }

    // ---- Coerce: char ----

    [Fact]
    public void Coerce_StringToChar_SingleChar_ReturnsChar()
    {
        TypeCoercer.Coerce("A", typeof(char)).Should().Be('A');
    }

    [Fact]
    public void Coerce_StringToChar_EmptyString_ReturnsNullChar()
    {
        TypeCoercer.Coerce("", typeof(char)).Should().Be('\0');
    }

    [Fact]
    public void Coerce_StringToChar_LongString_ThrowsInvalidCastException()
    {
        var act = () => TypeCoercer.Coerce("ab", typeof(char));
        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Coerce_IntToChar_ValidRange_ReturnsChar()
    {
        TypeCoercer.Coerce(65, typeof(char)).Should().Be('A');
    }

    // ---- Coerce: array ----

    [Fact]
    public void Coerce_SingleStringToStringArray_WrapsInArray()
    {
        var arr = (string[])TypeCoercer.Coerce("hello", typeof(string[]))!;
        arr.Should().Equal(new[] { "hello" });
    }

    [Fact]
    public void Coerce_ArrayToStringArray_ConvertsEachElement()
    {
        var source = new object[] { 1, "two", true };
        var arr = (string[])TypeCoercer.Coerce(source, typeof(string[]))!;
        arr.Should().Equal(new[] { "1", "two", "True" });
    }

    [Fact]
    public void Coerce_ObjectArrayToIntArray_ConvertsEachElement()
    {
        var source = new object[] { "1", 2, "3" };
        var arr = (int[])TypeCoercer.Coerce(source, typeof(int[]))!;
        arr.Should().Equal(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Coerce_EnumerableToObjectArray_ConvertsAll()
    {
        var list = new List<object> { 1, "two", 3.0 };
        var arr = (object[])TypeCoercer.Coerce(list, typeof(object[]))!;
        arr.Should().HaveCount(3);
        arr[0].Should().Be(1);
    }

    // ---- Coerce: hashtable ----

    [Fact]
    public void Coerce_HashtableToHashtable_PassesThrough()
    {
        var ht = new Hashtable { ["a"] = 1 };
        var result = (Hashtable)TypeCoercer.Coerce(ht, typeof(Hashtable))!;
        result.Should().BeSameAs(ht);
    }

    [Fact]
    public void Coerce_DictionaryToHashtable_ConvertsToCaseInsensitiveHashtable()
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Key"] = 1,
        };
        var result = (Hashtable)TypeCoercer.Coerce(dict, typeof(Hashtable))!;
        // Case-insensitive lookup should now work.
        result["KEY"].Should().Be(1);
    }

    // ---- Coerce: misc ----

    [Fact]
    public void Coerce_SameType_ReturnsSameValue()
    {
        TypeCoercer.Coerce(42, typeof(int)).Should().Be(42);
    }

    [Fact]
    public void Coerce_TargetObject_ReturnsValueUnchanged()
    {
        var obj = new object();
        TypeCoercer.Coerce(obj, typeof(object)).Should().BeSameAs(obj);
    }

    [Fact]
    public void Coerce_NullToNullableInt_ReturnsNull()
    {
        TypeCoercer.Coerce(null, typeof(int?)).Should().BeNull();
    }

    [Fact]
    public void Coerce_Failure_MessageContainsSourceAndTargetTypes()
    {
        var act = () => TypeCoercer.Coerce("not-a-number", typeof(int));
        act.Should().Throw<InvalidCastException>()
            .WithMessage("*System.String*System.Int32*");
    }

    // ---- ResolveTypeAnnotation ----

    [Theory]
    [InlineData("int", typeof(int))]
    [InlineData("integer", typeof(int))]
    [InlineData("long", typeof(long))]
    [InlineData("string", typeof(string))]
    [InlineData("str", typeof(string))]
    [InlineData("bool", typeof(bool))]
    [InlineData("boolean", typeof(bool))]
    [InlineData("double", typeof(double))]
    [InlineData("float", typeof(float))]
    [InlineData("single", typeof(float))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("datetime", typeof(DateTimeOffset))]
    [InlineData("string[]", typeof(string[]))]
    [InlineData("int[]", typeof(int[]))]
    [InlineData("object[]", typeof(object[]))]
    [InlineData("hashtable", typeof(Hashtable))]
    [InlineData("scriptblock", typeof(ScriptBlock))]
    [InlineData("switch", typeof(bool))]
    [InlineData("char", typeof(char))]
    [InlineData("object", typeof(object))]
    public void ResolveTypeAnnotation_KnownNames_ReturnsExpectedType(string annotation, Type expected)
    {
        TypeCoercer.ResolveTypeAnnotation(annotation).Should().Be(expected);
    }

    [Theory]
    [InlineData("PSCustomObject", typeof(object))]
    public void ResolveTypeAnnotation_PlaceholderTypes_ReturnsObject(string annotation, Type expected)
    {
        TypeCoercer.ResolveTypeAnnotation(annotation).Should().Be(expected);
    }

    [Fact]
    public void ResolveTypeAnnotation_NullOrWhitespace_ReturnsNull()
    {
        TypeCoercer.ResolveTypeAnnotation(null!).Should().BeNull();
        TypeCoercer.ResolveTypeAnnotation("").Should().BeNull();
        TypeCoercer.ResolveTypeAnnotation("   ").Should().BeNull();
    }

    [Fact]
    public void ResolveTypeAnnotation_SystemTypeFullName_ResolvesViaGetType()
    {
        TypeCoercer.ResolveTypeAnnotation("System.DateTimeOffset").Should().Be(typeof(DateTimeOffset));
    }

    [Fact]
    public void ResolveTypeAnnotation_Unknown_ReturnsNull()
    {
        TypeCoercer.ResolveTypeAnnotation("totally-not-a-type").Should().BeNull();
    }

    [Fact]
    public void ResolveTypeAnnotation_TrimsWhitespace()
    {
        TypeCoercer.ResolveTypeAnnotation("  int  ").Should().Be(typeof(int));
    }
}
