using System.Collections;
using FluentAssertions;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// HashLiteral 单元测试。Per ADR-0047 §6.
/// </summary>
public class HashLiteralTests
{
    [Fact]
    public void Create_BuildsCaseInsensitiveHashtable()
    {
        var ht = HashLiteral.Create(new[]
        {
            ("Name", (object?)"Alice"),
            ("Age", (object?)30),
        });
        ht.Count.Should().Be(2);
        // Case-insensitive lookup.
        ht["NAME"].Should().Be("Alice");
        ht["age"].Should().Be(30);
    }

    [Fact]
    public void Create_EmptyEntries_ReturnsEmptyHashtable()
    {
        var ht = HashLiteral.Create(Array.Empty<(string, object?)>());
        ht.Count.Should().Be(0);
    }

    [Fact]
    public void Create_NullEntries_ThrowsArgumentNullException()
    {
        var act = () => HashLiteral.Create(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_NullKey_ThrowsArgumentException()
    {
        var act = () => HashLiteral.Create(new (string, object?)[] { (null!, (object?)"value") });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_DuplicateKeys_LastWins()
    {
        var ht = HashLiteral.Create(new[]
        {
            ("Key", (object?)"first"),
            ("key", (object?)"second"), // case-insensitive duplicate
        });
        ht.Count.Should().Be(1);
        ht["Key"].Should().Be("second");
    }

    [Fact]
    public void Empty_ReturnsCaseInsensitiveEmptyHashtable()
    {
        var ht = HashLiteral.Empty();
        ht.Count.Should().Be(0);
        // Verify case-insensitive behavior.
        ht.Add("Foo", "Bar");
        ht["foo"].Should().Be("Bar");
    }

    [Fact]
    public void From_IDictionary_ConvertsToCaseInsensitive()
    {
        var source = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Key"] = 1,
        };
        var ht = HashLiteral.From(source);
        ht["KEY"].Should().Be(1);
    }

    [Fact]
    public void From_NullSource_ThrowsArgumentNullException()
    {
        var act = () => HashLiteral.From(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void From_Hashtable_DoesCaseInsensitiveShallowCopy()
    {
        var source = new Hashtable(StringComparer.Ordinal) { ["Original"] = "value" };
        var copy = HashLiteral.From(source);
        copy["ORIGINAL"].Should().Be("value");
        copy.Should().NotBeSameAs(source);
    }

    [Fact]
    public void Create_NullValue_PreservesNull()
    {
        var ht = HashLiteral.Create(new[] { ("k", (object?)null) });
        ht.Count.Should().Be(1);
        ht["k"].Should().BeNull();
    }

    [Fact]
    public void Create_MixedValueTypes_PreservesOriginalTypes()
    {
        var ht = HashLiteral.Create(new[]
        {
            ("Int", (object?)42),
            ("Str", (object?)"hello"),
            ("Bool", (object?)true),
            ("Arr", (object?)new[] { 1, 2, 3 }),
        });
        ht["Int"].Should().Be(42);
        ht["Str"].Should().Be("hello");
        ht["Bool"].Should().Be(true);
        ht["Arr"].Should().BeOfType<int[]>();
    }
}
