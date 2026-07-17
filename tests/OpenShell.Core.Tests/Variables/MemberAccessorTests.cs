using System.Collections;
using System.Collections.Immutable;
using FluentAssertions;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// MemberAccessor 单元测试。Per ADR-0047 §4.
/// </summary>
public class MemberAccessorTests
{
    // ---- GetProperty: CLR reflection ----

    [Fact]
    public void GetProperty_PublicProperty_ReturnsValue()
    {
        var target = new SampleObject { Name = "Alice", Age = 30 };
        MemberAccessor.GetProperty(target, "Name").Should().Be("Alice");
        MemberAccessor.GetProperty(target, "Age").Should().Be(30);
    }

    [Fact]
    public void GetProperty_CaseInsensitive_FindsProperty()
    {
        var target = new SampleObject { Name = "Bob" };
        MemberAccessor.GetProperty(target, "name").Should().Be("Bob");
        MemberAccessor.GetProperty(target, "NAME").Should().Be("Bob");
    }

    [Fact]
    public void GetProperty_NotFound_ThrowsMemberNotFoundException()
    {
        var target = new SampleObject();
        var act = () => MemberAccessor.GetProperty(target, "NonExistent");
        act.Should().Throw<MemberNotFoundException>()
            .Which.TargetType.Should().Be(typeof(SampleObject));
    }

    [Fact]
    public void GetProperty_NullTarget_ThrowsRuntimeBinderException()
    {
        var act = () => MemberAccessor.GetProperty(null, "Name");
        act.Should().Throw<RuntimeBinderException>();
    }

    [Fact]
    public void GetProperty_PublicField_ReturnsValue()
    {
        var target = new SampleWithField { Tag = "tag-value" };
        MemberAccessor.GetProperty(target, "Tag").Should().Be("tag-value");
    }

    // ---- GetProperty: IList / array ----

    [Fact]
    public void GetProperty_ArrayLength_ReturnsLongLength()
    {
        Array arr = new[] { 1, 2, 3 };
        MemberAccessor.GetProperty(arr, "Length").Should().Be(3);
        MemberAccessor.GetProperty(arr, "Count").Should().Be(3);
        MemberAccessor.GetProperty(arr, "LongLength").Should().Be(3L);
    }

    [Fact]
    public void GetProperty_ListCount_ReturnsCount()
    {
        IList list = new List<int> { 1, 2, 3 };
        MemberAccessor.GetProperty(list, "Count").Should().Be(3);
    }

    // ---- GetProperty: IDictionary ----

    [Fact]
    public void GetProperty_DictionaryByKey_ExactMatch()
    {
        var dict = new Hashtable(StringComparer.Ordinal) { ["Name"] = "Alice" };
        MemberAccessor.GetProperty(dict, "Name").Should().Be("Alice");
    }

    [Fact]
    public void GetProperty_DictionaryByKey_CaseInsensitiveFallback()
    {
        var dict = new Hashtable(StringComparer.Ordinal) { ["Name"] = "Bob" };
        MemberAccessor.GetProperty(dict, "NAME").Should().Be("Bob");
    }

    // ---- GetProperty: IItem ----

    [Fact]
    public void GetProperty_IItem_NameProperty_ReturnsItemName()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var item = Item.File(path);
        MemberAccessor.GetProperty(item, "Name").Should().Be("file.txt");
        MemberAccessor.GetProperty(item, "Kind").Should().Be(ItemKind.File);
    }

    [Fact]
    public void GetProperty_IItem_PropertyBagColumn_ReturnsValue()
    {
        var path = ItemPath.Parse("fs::/tmp/file.txt");
        var bag = new PropertyBag(ImmutableDictionary<string, object?>.Empty.Add("CustomColumn", "custom-value"));
        var item = new Item { Path = path, Kind = ItemKind.File, Properties = bag };
        MemberAccessor.GetProperty(item, "CustomColumn").Should().Be("custom-value");
    }

    // ---- GetIndex ----

    [Fact]
    public void GetIndex_IList_NonNegativeIndex_ReturnsElement()
    {
        IList list = new List<object> { "a", "b", "c" };
        MemberAccessor.GetIndex(list, 1).Should().Be("b");
    }

    [Fact]
    public void GetIndex_IList_NegativeIndex_FromEnd()
    {
        IList list = new List<object> { "a", "b", "c" };
        MemberAccessor.GetIndex(list, -1).Should().Be("c");
        MemberAccessor.GetIndex(list, -3).Should().Be("a");
    }

    [Fact]
    public void GetIndex_IList_OutOfRange_ThrowsIndexOutOfRangeException()
    {
        IList list = new List<object> { "a" };
        var act = () => MemberAccessor.GetIndex(list, 5);
        act.Should().Throw<IndexOutOfRangeException>();
    }

    [Fact]
    public void GetIndex_Array_NonNegativeIndex_ReturnsElement()
    {
        Array arr = new[] { 10, 20, 30 };
        MemberAccessor.GetIndex(arr, 0).Should().Be(10);
        MemberAccessor.GetIndex(arr, 2).Should().Be(30);
    }

    [Fact]
    public void GetIndex_Array_NegativeIndex_FromEnd()
    {
        Array arr = new[] { 10, 20, 30 };
        MemberAccessor.GetIndex(arr, -1).Should().Be(30);
    }

    [Fact]
    public void GetIndex_IDictionary_ByKey_ReturnsValue()
    {
        var dict = new Hashtable(StringComparer.Ordinal) { ["key"] = "value" };
        MemberAccessor.GetIndex(dict, "key").Should().Be("value");
    }

    [Fact]
    public void GetIndex_IDictionary_CaseInsensitiveLookup()
    {
        var dict = new Hashtable(StringComparer.Ordinal) { ["Key"] = "value" };
        MemberAccessor.GetIndex(dict, "KEY").Should().Be("value");
    }

    [Fact]
    public void GetIndex_IDictionary_MissingKey_ThrowsKeyNotFoundException()
    {
        var dict = new Hashtable(StringComparer.Ordinal) { ["key"] = "value" };
        var act = () => MemberAccessor.GetIndex(dict, "missing");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void GetIndex_NullTarget_ThrowsRuntimeBinderException()
    {
        var act = () => MemberAccessor.GetIndex(null, 0);
        act.Should().Throw<RuntimeBinderException>();
    }

    // ---- InvokeMethod ----

    [Fact]
    public void InvokeMethod_NoArguments_ReturnsResult()
    {
        var target = new SampleObject { Name = "Alice" };
        var result = MemberAccessor.InvokeMethod(target, "GetGreeting");
        result.Should().Be("Hello, Alice!");
    }

    [Fact]
    public void InvokeMethod_WithArguments_ReturnsResult()
    {
        var target = new SampleObject();
        var result = MemberAccessor.InvokeMethod(target, "Add", 3, 4);
        result.Should().Be(7);
    }

    [Fact]
    public void InvokeMethod_Overload_PicksByArgCount()
    {
        var target = new SampleObject();
        MemberAccessor.InvokeMethod(target, "Echo", "hello").Should().Be("hello");
        MemberAccessor.InvokeMethod(target, "Echo", "hello", "world").Should().Be("hello,world");
    }

    [Fact]
    public void InvokeMethod_NotFound_ThrowsMemberNotFoundException()
    {
        var target = new SampleObject();
        var act = () => MemberAccessor.InvokeMethod(target, "NonExistent");
        act.Should().Throw<MemberNotFoundException>()
            .Which.IsMethod.Should().BeTrue();
    }

    [Fact]
    public void InvokeMethod_ArgumentConversion_Applied()
    {
        var target = new SampleObject();
        // Pass string "5", method expects int.
        var result = MemberAccessor.InvokeMethod(target, "Add", "5", "6");
        result.Should().Be(11);
    }

    // ---- HasMember ----

    [Fact]
    public void HasMember_PublicProperty_ReturnsTrue()
    {
        var target = new SampleObject { Name = "Alice" };
        MemberAccessor.HasMember(target, "Name").Should().BeTrue();
    }

    [Fact]
    public void HasMember_Method_ReturnsTrue()
    {
        var target = new SampleObject();
        MemberAccessor.HasMember(target, "GetGreeting").Should().BeTrue();
    }

    [Fact]
    public void HasMember_Absent_ReturnsFalse()
    {
        var target = new SampleObject();
        MemberAccessor.HasMember(target, "NotHere").Should().BeFalse();
    }

    [Fact]
    public void HasMember_NullTarget_ReturnsFalse()
    {
        MemberAccessor.HasMember(null, "Name").Should().BeFalse();
    }

    // ---- Test fixtures ----

    private sealed class SampleObject
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }

        public string GetGreeting() => $"Hello, {Name}!";
        public int Add(int a, int b) => a + b;
        public string Echo(string s) => s;
        public string Echo(string a, string b) => $"{a},{b}";
    }

    private sealed class SampleWithField
    {
        public string Tag = "";
    }
}
