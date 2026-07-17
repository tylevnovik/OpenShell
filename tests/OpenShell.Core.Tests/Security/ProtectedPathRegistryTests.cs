using FluentAssertions;
using OpenShell.Paths;
using OpenShell.Security;
using Xunit;

namespace OpenShell.Core.Tests.Security;

/// <summary>
/// ADR-0036 §4: ProtectedPathRegistry 单测。
/// 验证:
/// - IsProtected: 匹配受保护路径前缀 (大小写不敏感)
/// - Add / Remove 动态扩展
/// - 默认值包含各平台系统目录
/// </summary>
public class ProtectedPathRegistryTests
{
    [Fact]
    public void Constructor_DefaultsContainWindowsSystemDirectories()
    {
        if (!OperatingSystem.IsWindows()) return;

        var registry = new ProtectedPathRegistry();

        registry.IsProtected(ItemPath.Parse("fs::C:/Windows")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::C:/Windows/System32")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::C:/Program Files")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::C:/Program Files (x86)")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("reg::HKLM/SAM")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("reg::HKLM/SECURITY")).Should().BeTrue();
    }

    [Fact]
    public void Constructor_DefaultsContainUnixSystemDirectories()
    {
        if (OperatingSystem.IsWindows()) return;

        var registry = new ProtectedPathRegistry();

        registry.IsProtected(ItemPath.Parse("fs::/etc")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::/usr/bin")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::/var/log")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::/boot/grub")).Should().BeTrue();
    }

    [Fact]
    public void IsProtected_PrefixMatch_CaseInsensitive()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/Sensitive" });

        // 大小写不敏感匹配。
        registry.IsProtected(ItemPath.Parse("fs::C:/sensitive/file.txt")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::C:/SENSITIVE")).Should().BeTrue();

        // 非前缀匹配不应触发。
        registry.IsProtected(ItemPath.Parse("fs::C:/Users")).Should().BeFalse();
    }

    [Fact]
    public void IsProtected_BackslashPath_Normalized()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:\\Sensitive" });

        registry.IsProtected(ItemPath.Parse("fs::C:/Sensitive/file.txt")).Should().BeTrue();
    }

    [Fact]
    public void IsProtected_TrailingSlash_Normalized()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/Sensitive/" });

        registry.IsProtected(ItemPath.Parse("fs::C:/Sensitive/file.txt")).Should().BeTrue();
        registry.IsProtected(ItemPath.Parse("fs::C:/Sensitive")).Should().BeTrue();
    }

    [Fact]
    public void IsProtected_ExactMatch_ReturnsTrue()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/MyApp" });

        registry.IsProtected(ItemPath.Parse("fs::C:/MyApp")).Should().BeTrue();
    }

    [Fact]
    public void IsProtected_NonPrefix_ReturnsFalse()
    {
        // 类似前缀但不是真正前缀 (例如路径分隔符不同)。
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/Sensitive" });

        registry.IsProtected(ItemPath.Parse("fs::C:/SensitiveApp/file.txt")).Should().BeFalse();
    }

    [Fact]
    public void Add_NewPath_IsIncludedInProtected()
    {
        var registry = new ProtectedPathRegistry(initialPaths: Array.Empty<string>());

        registry.IsProtected(ItemPath.Parse("fs::C:/MyApp")).Should().BeFalse();

        registry.Add("fs::C:/MyApp");

        registry.IsProtected(ItemPath.Parse("fs::C:/MyApp/config.json")).Should().BeTrue();
    }

    [Fact]
    public void Remove_Path_NoLongerProtected()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/MyApp" });

        registry.IsProtected(ItemPath.Parse("fs::C:/MyApp")).Should().BeTrue();

        registry.Remove("fs::C:/MyApp");

        registry.IsProtected(ItemPath.Parse("fs::C:/MyApp")).Should().BeFalse();
    }

    [Fact]
    public void Remove_IsCaseInsensitive()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/MyApp" });

        // 大小写不敏感删除
        registry.Remove("fs::C:/MYAPP");

        registry.IsProtected(ItemPath.Parse("fs::C:/MyApp")).Should().BeFalse();
    }

    [Fact]
    public void Add_IgnoresNullOrWhitespace()
    {
        var registry = new ProtectedPathRegistry(initialPaths: Array.Empty<string>());
        var initialCount = registry.List().Count;  // 含内置默认值

        var act = () => registry.Add("");

        act.Should().NotThrow();
        // 空 / 空白路径不应被加入, 数量不变。
        registry.List().Should().HaveCount(initialCount);
    }

    [Fact]
    public void Constructor_WithInitialPaths_PreservesOrderAndUniqueness()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[]
        {
            "fs::C:/A",
            "fs::C:/B",
            "fs::C:/A", // 重复, 应去重
        });

        var all = registry.List();
        var matchingA = all.Count(p => p.Equals("fs::C:/A", StringComparison.OrdinalIgnoreCase));
        matchingA.Should().Be(1, "duplicates should be removed");
    }

    [Fact]
    public void Constructor_WithInitialPaths_AugmentsBuiltinDefaults()
    {
        // 即使传入空集合, 内置默认值仍保留。
        var registry = new ProtectedPathRegistry(initialPaths: Array.Empty<string>());

        if (OperatingSystem.IsWindows())
        {
            registry.IsProtected(ItemPath.Parse("fs::C:/Windows")).Should().BeTrue();
        }
        else
        {
            registry.IsProtected(ItemPath.Parse("fs::/etc")).Should().BeTrue();
        }
    }

    [Fact]
    public void Constructor_NullInitialPaths_UsesOnlyBuiltinDefaults()
    {
        var registry = new ProtectedPathRegistry(initialPaths: null);

        // 内置默认值仍生效。
        if (OperatingSystem.IsWindows())
        {
            registry.IsProtected(ItemPath.Parse("fs::C:/Windows")).Should().BeTrue();
        }
        else
        {
            registry.IsProtected(ItemPath.Parse("fs::/etc")).Should().BeTrue();
        }
    }

    [Fact]
    public void List_ReturnsSnapshotCopy()
    {
        var registry = new ProtectedPathRegistry(initialPaths: new[] { "fs::C:/MyApp" });
        var snapshot = registry.List();

        // 修改 registry 不影响已返回的快照。
        registry.Add("fs::C:/Another");

        snapshot.Should().NotContain(p => p.Equals("fs::C:/Another", StringComparison.OrdinalIgnoreCase));
    }
}
