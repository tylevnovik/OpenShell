using System.Collections.Immutable;
using FluentAssertions;
using OpenShell.Items;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Providers;

/// <summary>
/// ProviderRegistry 单元测试。Per ADR-0001, ADR-0033.
/// </summary>
public class ProviderRegistryTests
{
    private static IProvider CreateProvider(string name) => new StubProvider(name);

    [Fact]
    public void Register_AddsProviderToList()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        registry.Registered.Should().HaveCount(1);
        registry.Registered.Single().Name.Should().Be("fs");
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        Action act = () => registry.Register(CreateProvider("fs"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Register_Duplicate_DifferentCase_Throws_BecauseCaseInsensitive()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        Action act = () => registry.Register(CreateProvider("FS"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Get_RegisteredProvider_ReturnsProvider()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        var p = registry.Get("fs");
        p.Info.Name.Should().Be("fs");
    }

    [Fact]
    public void Get_NotRegistered_Throws()
    {
        var registry = new ProviderRegistry();
        Action act = () => registry.Get("nonexistent");
        act.Should().Throw<ProviderNotFoundException>();
    }

    [Fact]
    public void Get_CaseInsensitive()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        var p = registry.Get("FS");
        p.Info.Name.Should().Be("fs");
    }

    [Fact]
    public void TryGet_Registered_ReturnsTrue()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        var found = registry.TryGet("fs", out var provider);
        found.Should().BeTrue();
        provider!.Info.Name.Should().Be("fs");
    }

    [Fact]
    public void TryGet_NotRegistered_ReturnsFalse()
    {
        var registry = new ProviderRegistry();
        var found = registry.TryGet("nonexistent", out var provider);
        found.Should().BeFalse();
        provider.Should().BeNull();
    }

    [Fact]
    public void Unregister_Existing_RemovesAndReturnsTrue()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        var removed = registry.Unregister("fs");
        removed.Should().BeTrue();
        registry.Registered.Should().BeEmpty();
    }

    [Fact]
    public void Unregister_NonExisting_ReturnsFalse()
    {
        var registry = new ProviderRegistry();
        var removed = registry.Unregister("nope");
        removed.Should().BeFalse();
    }

    [Fact]
    public void Resolve_AsCapability_ReturnsTypedView()
    {
        var registry = new ProviderRegistry();
        registry.Register(new StubCapabilityProvider());
        var item = registry.Resolve<IItemProvider>("stub");
        item.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_NotSupportedCapability_ReturnsNull()
    {
        var registry = new ProviderRegistry();
        registry.Register(new StubProvider("stub"));
        var item = registry.Resolve<IItemProvider>("stub");
        item.Should().BeNull();
    }

    [Fact]
    public void ResolveProvider_FromPath_ReturnsProvider()
    {
        var registry = new ProviderRegistry();
        registry.Register(CreateProvider("fs"));
        var path = new OpenShell.Paths.ItemPath { Provider = "fs", InternalPath = "/x" };
        var p = registry.ResolveProvider(path);
        p.Info.Name.Should().Be("fs");
    }

    [Fact]
    public void ResolveCapability_FromPath_ReturnsCapability()
    {
        var registry = new ProviderRegistry();
        registry.Register(new StubCapabilityProvider());
        var path = new OpenShell.Paths.ItemPath { Provider = "stub", InternalPath = "/x" };
        var cap = registry.ResolveCapability<IItemProvider>(path);
        cap.Should().NotBeNull();
    }

    private sealed class StubProvider : IProvider
    {
        public StubProvider(string name)
        {
            Info = new ProviderInfo { Name = name, Version = new Version(0, 1, 0) };
        }
        public ProviderInfo Info { get; }
        public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>();
    }

    private sealed class StubCapabilityProvider : IProvider, IItemProvider
    {
        public ProviderInfo Info { get; } = new() { Name = "stub", Version = new Version(0, 1, 0) };
        public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability> { ProviderCapability.Item };

        public ValueTask<IItem?> GetItemAsync(OpenShell.Paths.ItemPath path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IItem?>(null);
    }
}
