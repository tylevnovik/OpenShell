using FluentAssertions;
using OpenShell.Providers;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Providers;

/// <summary>
/// InMemoryDriveRegistry 单元测试。Per ADR-0023, ADR-0033.
/// </summary>
public class InMemoryDriveRegistryTests
{
    private static ProviderDrive CreateDrive(string name) => new()
    {
        Name = name,
        Root = new ItemPath { Provider = "fs", InternalPath = "/" },
    };

    [Fact]
    public void Mount_AddsDriveToList()
    {
        var registry = new InMemoryDriveRegistry();
        registry.Mount(CreateDrive("C:"));
        registry.Mounted.Should().HaveCount(1);
        registry.Mounted[0].Name.Should().Be("C:");
    }

    [Fact]
    public void Mount_Duplicate_Overwrites()
    {
        var registry = new InMemoryDriveRegistry();
        registry.Mount(CreateDrive("C:"));
        registry.Mount(CreateDrive("C:"));
        registry.Mounted.Should().HaveCount(1);
    }

    [Fact]
    public void Mount_EmptyName_Throws()
    {
        var registry = new InMemoryDriveRegistry();
        var drive = new ProviderDrive
        {
            Name = " ",
            Root = new ItemPath { Provider = "fs", InternalPath = "/" },
        };
        Action act = () => registry.Mount(drive);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unmount_Existing_RemovesAndReturnsTrue()
    {
        var registry = new InMemoryDriveRegistry();
        registry.Mount(CreateDrive("C:"));
        var removed = registry.Unmount("C:");
        removed.Should().BeTrue();
        registry.Mounted.Should().BeEmpty();
    }

    [Fact]
    public void Unmount_NonExisting_ReturnsFalse()
    {
        var registry = new InMemoryDriveRegistry();
        var removed = registry.Unmount("Z:");
        removed.Should().BeFalse();
    }

    [Fact]
    public void Find_Existing_ReturnsDrive()
    {
        var registry = new InMemoryDriveRegistry();
        registry.Mount(CreateDrive("C:"));
        var found = registry.Find("C:");
        found.Should().NotBeNull();
        found!.Name.Should().Be("C:");
    }

    [Fact]
    public void Find_NonExisting_ReturnsNull()
    {
        var registry = new InMemoryDriveRegistry();
        var found = registry.Find("Z:");
        found.Should().BeNull();
    }

    [Fact]
    public void Find_CaseInsensitive()
    {
        var registry = new InMemoryDriveRegistry();
        registry.Mount(CreateDrive("C:"));
        var found = registry.Find("c:");
        found.Should().NotBeNull();
    }

    [Fact]
    public void MountedChanged_RaisedOnMount()
    {
        var registry = new InMemoryDriveRegistry();
        ProviderDrive? raised = null;
        registry.MountedChanged += (_, d) => raised = d;
        registry.Mount(CreateDrive("C:"));
        raised.Should().NotBeNull();
        raised!.Name.Should().Be("C:");
    }

    [Fact]
    public void MountedChanged_RaisedOnUnmount()
    {
        var registry = new InMemoryDriveRegistry();
        registry.Mount(CreateDrive("C:"));
        ProviderDrive? raised = null;
        registry.MountedChanged += (_, d) => raised = d;
        registry.Unmount("C:");
        raised.Should().NotBeNull();
        raised!.Name.Should().Be("C:");
    }
}
