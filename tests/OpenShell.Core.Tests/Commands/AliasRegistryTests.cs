using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Errors;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// AliasRegistry 单元测试。Per ADR-0024, ADR-0033.
/// </summary>
public class AliasRegistryTests
{
    private static AliasRegistry Create() => new(userGlobalDir: "/nonexistent", projectDir: "/nonexistent");

    [Fact]
    public void Resolve_UnknownName_ReturnsNull()
    {
        var registry = Create();
        registry.Resolve("nonexistent").Should().BeNull();
    }

    [Fact]
    public void ResolveFunction_UnknownName_ReturnsNull()
    {
        var registry = Create();
        registry.ResolveFunction("nonexistent").Should().BeNull();
    }

    [Fact]
    public void SetSessionAlias_ValidName_ResolveReturnsIt()
    {
        var registry = Create();
        registry.SetSessionAlias("ll", "get-childitem -l");
        var resolved = registry.Resolve("ll");
        resolved.Should().NotBeNull();
        resolved!.Command.Should().Be("get-childitem -l");
        resolved.Source.Should().Be(AliasSource.Session);
    }

    [Fact]
    public void SetSessionAlias_OverwritesPrevious()
    {
        var registry = Create();
        registry.SetSessionAlias("g", "get-item");
        registry.SetSessionAlias("g", "get-childitem");
        registry.Resolve("g")!.Command.Should().Be("get-childitem");
    }

    [Fact]
    public void SetSessionAlias_NameContainsDash_ThrowsConfigurationError()
    {
        var registry = Create();
        var act = () => registry.SetSessionAlias("my-alias", "get-item");
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void SetSessionAlias_NameStartsWithDigit_ThrowsConfigurationError()
    {
        var registry = Create();
        var act = () => registry.SetSessionAlias("1alias", "get-item");
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void SetSessionAlias_EmptyName_ThrowsConfigurationError()
    {
        var registry = Create();
        var act = () => registry.SetSessionAlias("", "get-item");
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void SetSessionAlias_EmptyCommand_ThrowsConfigurationError()
    {
        var registry = Create();
        var act = () => registry.SetSessionAlias("x", "");
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void RemoveSessionAlias_Existing_RemovesAndReturnsTrue()
    {
        var registry = Create();
        registry.SetSessionAlias("temp", "get-item");
        var result = registry.RemoveSessionAlias("temp");
        result.Should().BeTrue();
        registry.Resolve("temp").Should().BeNull();
    }

    [Fact]
    public void RemoveSessionAlias_NonExisting_ReturnsFalse()
    {
        var registry = Create();
        registry.RemoveSessionAlias("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void SetSessionFunction_ValidBody_ResolveFunctionReturnsIt()
    {
        var registry = Create();
        registry.SetSessionFunction(new UserFunction
        {
            Name = "greet",
            Body = "write-output hello",
        });
        var fn = registry.ResolveFunction("greet");
        fn.Should().NotBeNull();
        fn!.Body.Should().Be("write-output hello");
        fn.Source.Should().Be(AliasSource.Session);
    }

    [Fact]
    public void SetSessionFunction_BodyContainsExit_Throws()
    {
        var registry = Create();
        var act = () => registry.SetSessionFunction(new UserFunction
        {
            Name = "bad",
            Body = "exit 1",
        });
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void SetSessionFunction_BodyContainsReturn_Throws()
    {
        var registry = Create();
        var act = () => registry.SetSessionFunction(new UserFunction
        {
            Name = "bad2",
            Body = "write-output start; return",
        });
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void RemoveSessionFunction_Existing_RemovesAndReturnsTrue()
    {
        var registry = Create();
        registry.SetSessionFunction(new UserFunction { Name = "removable", Body = "write-output ok" });
        var result = registry.RemoveSessionFunction("removable");
        result.Should().BeTrue();
        registry.ResolveFunction("removable").Should().BeNull();
    }

    [Fact]
    public void List_IncludesSessionAlias()
    {
        var registry = Create();
        registry.SetSessionAlias("listme", "get-childitem");
        var list = registry.List();
        list.Should().Contain(a => a.Name == "listme");
    }

    [Fact]
    public void ListUserDefined_ExcludesBuiltins()
    {
        var registry = Create();
        registry.SetSessionAlias("ud", "get-childitem");
        var list = registry.ListUserDefined();
        list.Should().Contain(a => a.Name == "ud");
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var registry = Create();
        registry.SetSessionAlias("CamelCase", "get-childitem");
        registry.Resolve("camelcase").Should().NotBeNull();
        registry.Resolve("CAMELCASE").Should().NotBeNull();
    }

    [Fact]
    public void PopulateBuiltinsFrom_RegistersAliasesFromDescriptors()
    {
        var registry = Create();
        var commands = new CommandRegistry();
        var descriptor = CommandDescriptor.FromType(typeof(StubWithAliasCommand));
        commands.Register(descriptor);

        registry.PopulateBuiltinsFrom(commands);

        registry.Builtins.Should().ContainKey("sca");
        var resolved = registry.Resolve("sca");
        resolved.Should().NotBeNull();
        resolved!.Source.Should().Be(AliasSource.Builtin);
        resolved.Command.Should().Be("stub-command");
    }

    [Fact]
    public void PopulateBuiltinsFrom_MkdirAlias_AppendsTypeDirectory()
    {
        var registry = Create();
        var commands = new CommandRegistry();
        var descriptor = CommandDescriptor.FromType(typeof(StubNewItemCommand));
        commands.Register(descriptor);

        registry.PopulateBuiltinsFrom(commands);

        // D-315: 使用 -type:directory 冒号形式（NamedParameter 语法），避免空格形式被误解析。
        registry.Builtins["mkdir"].Should().Be("stub-newitem -type:directory");
    }

    [Fact]
    public void PopulateBuiltinsFrom_TouchAlias_AppendsTypeFile()
    {
        var registry = Create();
        var commands = new CommandRegistry();
        var descriptor = CommandDescriptor.FromType(typeof(StubNewItemCommand));
        commands.Register(descriptor);

        registry.PopulateBuiltinsFrom(commands);

        // D-315: 使用 -type:file 冒号形式（NamedParameter 语法），避免空格形式被误解析。
        registry.Builtins["touch"].Should().Be("stub-newitem -type:file");
    }

    [Fact]
    public void SetSessionAlias_CircularAlias_ThrowsConfigurationError()
    {
        // a → b → a 形成循环
        var registry = Create();
        registry.SetSessionAlias("a", "b");
        var act = () => registry.SetSessionAlias("b", "a");
        act.Should().Throw<ConfigurationErrorException>();
    }

    [Fact]
    public void DefaultUserGlobalDir_ContainsOpenShellFolder()
    {
        var dir = AliasRegistry.DefaultUserGlobalDir();
        dir.Should().Contain(".openshell");
    }

    [Verb("Stub", Noun = "Command", Aliases = new[] { "sca" })]
    private sealed class StubWithAliasCommand
    {
        public class Args { }
    }

    [Verb("Stub", Noun = "NewItem", Aliases = new[] { "mkdir", "touch" })]
    private sealed class StubNewItemCommand
    {
        public class Args { }
    }
}
