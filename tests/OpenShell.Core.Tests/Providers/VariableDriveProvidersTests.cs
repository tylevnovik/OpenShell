using System.Text;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers.Variables;
using OpenShell.Variables;
using Xunit;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Core.Tests.Providers;

/// <summary>
/// VariableProvider / EnvProvider / FunctionProvider 集成测试。Per ADR-0047 §10.
/// </summary>
public class VariableDriveProvidersTests
{
    // ---- VariableProvider ----

    [Fact]
    public async Task Variable_GetItem_ReturnsVariableValue()
    {
        var vars = new InMemoryVariableRegistry();
        vars.Set("MyVar", "hello");
        var provider = new VariableProvider(vars);

        var item = await provider.GetItemAsync(ItemPath.Parse("variable::MyVar"));

        item.Should().NotBeNull();
        item!.Properties["Value"].Should().Be("hello");
        item.Properties["Name"].Should().Be("MyVar");
        item.Properties["Options"].Should().Be("None");
    }

    [Fact]
    public async Task Variable_GetItem_NonExistent_ReturnsNull()
    {
        var vars = new InMemoryVariableRegistry();
        var provider = new VariableProvider(vars);

        var item = await provider.GetItemAsync(ItemPath.Parse("variable::NonExistent"));

        item.Should().BeNull();
    }

    [Fact]
    public async Task Variable_GetChildren_ListsAllVariables()
    {
        var vars = new InMemoryVariableRegistry();
        vars.Set("A", 1);
        vars.Set("B", "two");
        var provider = new VariableProvider(vars);

        var children = new List<IItem>();
        await foreach (var item in provider.GetChildrenAsync(ItemPath.Root("variable"), new EnumerationOptions()))
            children.Add(item);

        children.Select(i => i.Properties["Name"]?.ToString())
            .Should().Contain(new[] { "A", "B" });
    }

    [Fact]
    public async Task Variable_Delete_RemovesVariable()
    {
        var vars = new InMemoryVariableRegistry();
        vars.Set("ToDelete", "value");
        var provider = new VariableProvider(vars);

        await provider.DeleteAsync(ItemPath.Parse("variable::ToDelete"), recurse: false);

        vars.Resolve("ToDelete").Should().BeNull();
    }

    [Fact]
    public async Task Variable_Rename_MovesValue()
    {
        var vars = new InMemoryVariableRegistry();
        vars.Set("OldName", "value");
        var provider = new VariableProvider(vars);

        await provider.RenameAsync(ItemPath.Parse("variable::OldName"), "NewName");

        vars.Resolve("OldName").Should().BeNull();
        vars.Resolve("NewName").Should().Be("value");
    }

    [Fact]
    public async Task Variable_OpenRead_ReturnsValueAsStream()
    {
        var vars = new InMemoryVariableRegistry();
        vars.Set("MyVar", "hello world");
        var provider = new VariableProvider(vars);

        var stream = await provider.OpenReadAsync(ItemPath.Parse("variable::MyVar"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();

        text.Should().Be("hello world");
    }

    [Fact]
    public async Task Variable_OpenWrite_SetsVariableFromStream()
    {
        var vars = new InMemoryVariableRegistry();
        var provider = new VariableProvider(vars);

        var stream = await provider.OpenWriteAsync(ItemPath.Parse("variable::Written"));
        var bytes = Encoding.UTF8.GetBytes("written value");
        await stream.WriteAsync(bytes);
        stream.Dispose();

        vars.Resolve("Written").Should().Be("written value");
    }

    [Fact]
    public async Task Variable_GetDrives_ReturnsVariableDrive()
    {
        var provider = new VariableProvider(new InMemoryVariableRegistry());

        var drives = await provider.GetDrivesAsync();

        drives.Should().ContainSingle(d => d.Name == "Variable:");
    }

    [Fact]
    public async Task Variable_ReadOnly_ShowsInOptions()
    {
        var vars = new InMemoryVariableRegistry();
        vars.SetAutomatic("?", true); // ? is an automatic (read-only) variable
        var provider = new VariableProvider(vars);

        var item = await provider.GetItemAsync(ItemPath.Parse("variable::?"));

        item!.Properties["Options"].Should().Be("ReadOnly");
    }

    // ---- EnvProvider ----

    [Fact]
    public async Task Env_GetItem_ReturnsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("OPENSHELL_TEST_ENV_1", "env value");
        try
        {
            var provider = new EnvProvider();

            var item = await provider.GetItemAsync(ItemPath.Parse("env::OPENSHELL_TEST_ENV_1"));

            item.Should().NotBeNull();
            item!.Properties["Value"].Should().Be("env value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_TEST_ENV_1", null);
        }
    }

    [Fact]
    public async Task Env_GetChildren_ListsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("OPENSHELL_TEST_ENV_2", "list value");
        try
        {
            var provider = new EnvProvider();

            var children = new List<IItem>();
            await foreach (var item in provider.GetChildrenAsync(ItemPath.Root("env"), new EnumerationOptions()))
                children.Add(item);

            children.Select(i => i.Properties["Name"]?.ToString())
                .Should().Contain("OPENSHELL_TEST_ENV_2");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_TEST_ENV_2", null);
        }
    }

    [Fact]
    public async Task Env_Delete_RemovesEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("OPENSHELL_TEST_ENV_3", "to delete");
        var provider = new EnvProvider();

        await provider.DeleteAsync(ItemPath.Parse("env::OPENSHELL_TEST_ENV_3"), recurse: false);

        Environment.GetEnvironmentVariable("OPENSHELL_TEST_ENV_3").Should().BeNull();
    }

    [Fact]
    public async Task Env_OpenWrite_SetsEnvironmentVariable()
    {
        var provider = new EnvProvider();

        var stream = await provider.OpenWriteAsync(ItemPath.Parse("env::OPENSHELL_TEST_ENV_4"));
        var bytes = Encoding.UTF8.GetBytes("new env value");
        await stream.WriteAsync(bytes);
        stream.Dispose();

        try
        {
            Environment.GetEnvironmentVariable("OPENSHELL_TEST_ENV_4").Should().Be("new env value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_TEST_ENV_4", null);
        }
    }

    [Fact]
    public async Task Env_GetDrives_ReturnsEnvDrive()
    {
        var provider = new EnvProvider();

        var drives = await provider.GetDrivesAsync();

        drives.Should().ContainSingle(d => d.Name == "Env:");
    }

    // ---- FunctionProvider ----

    [Fact]
    public async Task Function_GetItem_ReturnsFunctionBody()
    {
        var aliases = new AliasRegistry();
        aliases.SetSessionFunction(new UserFunction
        {
            Name = "greet",
            Body = "Write-Host 'Hello'",
            Parameters = new[] { "name" },
        });
        var provider = new FunctionProvider(aliases);

        var item = await provider.GetItemAsync(ItemPath.Parse("function::greet"));

        item.Should().NotBeNull();
        item!.Properties["Body"].Should().Be("Write-Host 'Hello'");
        item.Properties["Parameters"].Should().Be("name");
    }

    [Fact]
    public async Task Function_GetChildren_ListsAllFunctions()
    {
        var aliases = new AliasRegistry();
        aliases.SetSessionFunction(new UserFunction { Name = "fn1", Body = "body1" });
        aliases.SetSessionFunction(new UserFunction { Name = "fn2", Body = "body2" });
        var provider = new FunctionProvider(aliases);

        var children = new List<IItem>();
        await foreach (var item in provider.GetChildrenAsync(ItemPath.Root("function"), new EnumerationOptions()))
            children.Add(item);

        children.Select(i => i.Properties["Name"]?.ToString())
            .Should().Contain(new[] { "fn1", "fn2" });
    }

    [Fact]
    public async Task Function_Delete_RemovesFunction()
    {
        var aliases = new AliasRegistry();
        aliases.SetSessionFunction(new UserFunction { Name = "ToRemove", Body = "body" });
        var provider = new FunctionProvider(aliases);

        await provider.DeleteAsync(ItemPath.Parse("function::ToRemove"), recurse: false);

        aliases.ResolveFunction("ToRemove").Should().BeNull();
    }

    [Fact]
    public async Task Function_Rename_MovesFunction()
    {
        var aliases = new AliasRegistry();
        aliases.SetSessionFunction(new UserFunction { Name = "OldFn", Body = "body" });
        var provider = new FunctionProvider(aliases);

        await provider.RenameAsync(ItemPath.Parse("function::OldFn"), "NewFn");

        aliases.ResolveFunction("OldFn").Should().BeNull();
        aliases.ResolveFunction("NewFn").Should().NotBeNull();
        aliases.ResolveFunction("NewFn")!.Body.Should().Be("body");
    }

    [Fact]
    public async Task Function_OpenWrite_CreatesFunctionFromBody()
    {
        var aliases = new AliasRegistry();
        var provider = new FunctionProvider(aliases);

        var stream = await provider.OpenWriteAsync(ItemPath.Parse("function::NewFromStream"));
        var bytes = Encoding.UTF8.GetBytes("Write-Host 'stream created'");
        await stream.WriteAsync(bytes);
        stream.Dispose();

        var fn = aliases.ResolveFunction("NewFromStream");
        fn.Should().NotBeNull();
        fn!.Body.Should().Be("Write-Host 'stream created'");
    }

    [Fact]
    public async Task Function_GetDrives_ReturnsFunctionDrive()
    {
        var provider = new FunctionProvider(new AliasRegistry());

        var drives = await provider.GetDrivesAsync();

        drives.Should().ContainSingle(d => d.Name == "Function:");
    }
}
