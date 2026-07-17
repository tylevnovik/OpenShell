using FluentAssertions;
using OpenShell.Packaging;
using OpenShell.Packaging.Installation;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Packaging;

/// <summary>
/// ADR-0039 §7: 依赖解析单测。拓扑排序 + 版本范围解析。
/// </summary>
public class DependencyResolverTests
{
    private readonly DependencyResolver _resolver = new();

    [Fact]
    public void TopologicalSort_Empty_ReturnsEmpty()
    {
        _resolver.TopologicalSort(Array.Empty<ProviderManifest>()).Should().BeEmpty();
    }

    [Fact]
    public void TopologicalSort_NoDependencies_ReturnsSameSet()
    {
        var a = Manifest("a", Array.Empty<(string, string)>());
        var b = Manifest("b", Array.Empty<(string, string)>());
        var result = _resolver.TopologicalSort(new[] { a, b });
        result.Should().HaveCount(2);
    }

    [Fact]
    public void TopologicalSort_DependencyBeforeDependent()
    {
        // b 依赖 a → a 应排前面。
        var a = Manifest("a", Array.Empty<(string, string)>());
        var b = Manifest("b", new[] { ("a", ">= 1.0.0") });
        var result = _resolver.TopologicalSort(new[] { b, a });
        var names = result.Select(m => m.Name).ToList();
        names.IndexOf("a").Should().BeLessThan(names.IndexOf("b"));
    }

    [Fact]
    public void TopologicalSort_ChainedDependencies_OrderedCorrectly()
    {
        // c → b → a (链式)。
        var a = Manifest("a", Array.Empty<(string, string)>());
        var b = Manifest("b", new[] { ("a", ">= 1.0.0") });
        var c = Manifest("c", new[] { ("b", ">= 1.0.0") });
        var result = _resolver.TopologicalSort(new[] { c, b, a });
        var names = result.Select(m => m.Name).ToList();
        names.Should().Equal(new[] { "a", "b", "c" });
    }

    [Fact]
    public void TopologicalSort_CircularDependency_Throws()
    {
        // a → b → a (循环)。
        var a = Manifest("a", new[] { ("b", ">= 1.0.0") });
        var b = Manifest("b", new[] { ("a", ">= 1.0.0") });
        var act = () => _resolver.TopologicalSort(new[] { a, b });
        act.Should().Throw<OspPackageException>().WithMessage("*Circular*");
    }

    [Fact]
    public void TopologicalSort_ExternalDependenciesIgnoredInOrdering()
    {
        // external kind 依赖不参与排序 (运行时由 NuGet 解析)。
        var a = Manifest("a", new[] { ("Newtonsoft.Json", ">= 13.0.0", "external") });
        var result = _resolver.TopologicalSort(new[] { a });
        result.Should().ContainSingle();
    }

    [Fact]
    public void Resolve_MarksSatisfiedProviderDependencies()
    {
        var a = Manifest("a", new[] { ("b", ">= 1.0.0"), ("c", ">= 2.0.0") });
        var installed = new Dictionary<string, string>
        {
            ["b"] = "1.5.0",
            ["c"] = "1.0.0",  // 太老, 不满足 >= 2.0.0
        };
        var result = _resolver.Resolve(a, installed);
        result.Should().HaveCount(2);
        result.First(d => d.Name == "b").Satisfied.Should().BeTrue();
        result.First(d => d.Name == "b").ResolvedVersion.Should().Be("1.5.0");
        result.First(d => d.Name == "c").Satisfied.Should().BeFalse();
        result.First(d => d.Name == "c").ResolvedVersion.Should().BeNull();
    }

    [Fact]
    public void Resolve_ExternalDependenciesAlwaysUnsatisfied()
    {
        var a = Manifest("a", new[] { ("AWSSDK.S3", ">= 3.7.0", "external") });
        var installed = new Dictionary<string, string>();
        var result = _resolver.Resolve(a, installed);
        result.Should().ContainSingle();
        result[0].Kind.Should().Be("external");
        result[0].Satisfied.Should().BeFalse();
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]          // 精确匹配
    [InlineData("1.2.0", ">= 1.0.0", true)]
    [InlineData("1.2.0", ">= 1.2.0", true)]
    [InlineData("1.1.0", ">= 1.2.0", false)]       // 太老
    [InlineData("2.0.0", ">= 1.0.0 < 2.0.0", false)] // 超出上限
    [InlineData("1.5.0", ">= 1.0.0 < 2.0.0", true)]
    [InlineData("1.5.0", "[1.0,2.0)", true)]        // NuGet 区间
    [InlineData("2.0.0", "[1.0,2.0)", false)]       // 区间外
    [InlineData("1.5.0", "*", true)]               // 通配
    [InlineData("1.5.0", "", true)]                // 空 = 任意
    [InlineData("1.0.0", "[1.0]", true)]           // 精确区间
    [InlineData("1.1.0", "[1.0]", false)]          // 精确区间不匹配
    public void IsSatisfied_VersionRange(string concrete, string range, bool expected)
    {
        DependencyResolver.IsSatisfied(concrete, range).Should().Be(expected);
    }

    [Fact]
    public void IsSatisfied_InvalidConcreteVersion_ReturnsFalse()
    {
        DependencyResolver.IsSatisfied("not-a-version", ">= 1.0.0").Should().BeFalse();
    }

    private static ProviderManifest Manifest(string name, IReadOnlyList<(string Name, string Version, string Kind)> deps)
    {
        var list = deps.Select(d => new ProviderDependency { Name = d.Name, Version = d.Version, Kind = d.Kind }).ToList();
        return new ProviderManifest
        {
            Name = name,
            Version = "1.0.0",
            RequiredApiVersion = "1.0.0",
            Dependencies = list,
        };
    }

    private static ProviderManifest Manifest(string name, IReadOnlyList<(string Name, string Version)> deps)
        => Manifest(name, deps.Select(d => (d.Name, d.Version, "provider")).ToList());
}
