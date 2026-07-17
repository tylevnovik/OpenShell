using System.Reflection;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// CommandRegistry 单元测试。Per ADR-0004, ADR-0033.
/// </summary>
public class CommandRegistryTests
{
    private static CommandDescriptor CreateDescriptor(string verb, string noun, string[]? aliases = null)
    {
        // 通过 Reflection 创建带 [Verb] 的临时类型。
        var type = typeof(CommandRegistryTests).GetNestedType("SampleCommand", BindingFlags.NonPublic)!;
        // 由于 [Verb] 在编译期固定，测试中直接使用 GetChildItemCommand 的真实类型。
        var desc = CommandDescriptor.FromType(typeof(GetChildItemCommand));
        // 修改 FullName 字段比较用：手工构造不同的全名。
        return desc with { Verb = verb, Noun = noun, FullName = $"{verb.ToLower()}-{noun.ToLower()}", Aliases = aliases ?? Array.Empty<string>() };
    }

    [Fact]
    public void Register_AddsToList()
    {
        var registry = new CommandRegistry();
        var desc = CommandDescriptor.FromType(typeof(GetChildItemCommand));
        registry.Register(desc);
        registry.Registered.Should().HaveCount(1);
    }

    [Fact]
    public void Register_DuplicateFullName_Throws()
    {
        var registry = new CommandRegistry();
        var desc = CommandDescriptor.FromType(typeof(GetChildItemCommand));
        registry.Register(desc);
        Action act = () => registry.Register(desc);
        act.Should().Throw<DuplicateCommandException>();
    }

    [Fact]
    public void Resolve_ByFullName_ReturnsDescriptor()
    {
        var registry = new CommandRegistry();
        registry.Register(CommandDescriptor.FromType(typeof(GetChildItemCommand)));
        var desc = registry.Resolve("get-childitem");
        desc.Should().NotBeNull();
        desc!.Verb.Should().Be("Get");
    }

    [Fact]
    public void Resolve_ByAlias_ReturnsDescriptor()
    {
        var registry = new CommandRegistry();
        registry.Register(CommandDescriptor.FromType(typeof(GetChildItemCommand)));
        var desc = registry.Resolve("ls");
        desc.Should().NotBeNull();
        desc!.FullName.Should().Be("get-childitem");
    }

    [Fact]
    public void Resolve_AliasWithDash_Prefix_Stripped()
    {
        var registry = new CommandRegistry();
        registry.Register(CommandDescriptor.FromType(typeof(GetChildItemCommand)));
        var desc = registry.Resolve("-ls");
        desc.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_FullNameWithDash_Prefix_Stripped()
    {
        var registry = new CommandRegistry();
        registry.Register(CommandDescriptor.FromType(typeof(GetChildItemCommand)));
        var desc = registry.Resolve("-get-childitem");
        desc.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_NotFound_ReturnsNull()
    {
        var registry = new CommandRegistry();
        var desc = registry.Resolve("nonexistent");
        desc.Should().BeNull();
    }

    [Fact]
    public void Resolve_EmptyOrWhitespace_ReturnsNull()
    {
        var registry = new CommandRegistry();
        registry.Resolve("").Should().BeNull();
        registry.Resolve("   ").Should().BeNull();
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var registry = new CommandRegistry();
        registry.Register(CommandDescriptor.FromType(typeof(GetChildItemCommand)));
        var desc = registry.Resolve("GET-CHILDITEM");
        desc.Should().NotBeNull();
    }

    [Fact]
    public void RegisterFromAssembly_ReturnsRegisteredCount()
    {
        var registry = new CommandRegistry();
        var count = registry.RegisterFromAssembly(typeof(GetChildItemCommand).Assembly);
        count.Should().BeGreaterThan(0);
        registry.Registered.Should().NotBeEmpty();
    }

    [Fact]
    public void RegisterFromAssembly_DuplicateCommands_Throws()
    {
        var registry = new CommandRegistry();
        registry.RegisterFromAssembly(typeof(GetChildItemCommand).Assembly);
        Action act = () => registry.RegisterFromAssembly(typeof(GetChildItemCommand).Assembly);
        act.Should().Throw<DuplicateCommandException>();
    }

    [Fact]
    public void RegisterTypes_SkipsDuplicatesWithoutThrowing()
    {
        var registry = new CommandRegistry();
        var types = new[] { typeof(GetChildItemCommand) };
        registry.RegisterTypes(types);
        var secondCount = registry.RegisterTypes(types);
        // 第二次注册应跳过已存在的，返回 0
        secondCount.Should().Be(0);
    }

    [Fact]
    public void RegisterTypes_NonCommandType_Skipped()
    {
        var registry = new CommandRegistry();
        var count = registry.RegisterTypes(new[] { typeof(string) });
        count.Should().Be(0);
    }

    [Fact]
    public void UnregisterTypes_RemovesFromRegistry()
    {
        var registry = new CommandRegistry();
        registry.RegisterTypes(new[] { typeof(GetChildItemCommand) });
        var removed = registry.UnregisterTypes(new[] { typeof(GetChildItemCommand) });
        removed.Should().Be(1);
        registry.Resolve("get-childitem").Should().BeNull();
    }

    [Fact]
    public void UnregisterTypes_NonRegistered_ReturnsZero()
    {
        var registry = new CommandRegistry();
        var removed = registry.UnregisterTypes(new[] { typeof(GetChildItemCommand) });
        removed.Should().Be(0);
    }

    [Fact]
    public void UnregisterTypes_AlsoRemovesAliasIndex()
    {
        var registry = new CommandRegistry();
        registry.RegisterTypes(new[] { typeof(GetChildItemCommand) });
        registry.Resolve("ls").Should().NotBeNull();
        registry.UnregisterTypes(new[] { typeof(GetChildItemCommand) });
        registry.Resolve("ls").Should().BeNull();
    }
}
