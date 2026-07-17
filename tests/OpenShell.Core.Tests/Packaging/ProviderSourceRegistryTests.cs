using FluentAssertions;
using OpenShell.Packaging.Registry;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Packaging;

/// <summary>
/// ADR-0039 §3: 注册源 TOML 配置单测。读写 / add / remove。
/// </summary>
public class ProviderSourceRegistryTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var reg = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "registries.toml"));
        await reg.LoadAsync();
        reg.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLoad_RoundTripsSources()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "registries.toml");
        var reg = new ProviderSourceRegistry(path);
        reg.AddSource(new ProviderSource { Name = "official", Url = "https://registry.openshell.dev/v1/", Priority = 1, Trusted = true });
        reg.AddSource(new ProviderSource { Name = "private", Url = "https://npm.corp.example.com/openshell/", Priority = 2, Trusted = false, Auth = "env:CORP_TOKEN" });

        await reg.SaveAsync();

        var reg2 = new ProviderSourceRegistry(path);
        await reg2.LoadAsync();
        reg2.Sources.Should().HaveCount(2);
        var official = reg2.Sources.First(s => s.Name == "official");
        official.Url.Should().Be("https://registry.openshell.dev/v1/");
        official.Priority.Should().Be(1);
        official.Trusted.Should().BeTrue();
        var priv = reg2.Sources.First(s => s.Name == "private");
        priv.Auth.Should().Be("env:CORP_TOKEN");
    }

    [Fact]
    public async Task AddSource_DuplicateName_Throws()
    {
        using var dir = new TempDir();
        var reg = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "r.toml"));
        reg.AddSource(new ProviderSource { Name = "official", Url = "https://x" });

        var act = () => reg.AddSource(new ProviderSource { Name = "official", Url = "https://y" });
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void AddSource_MissingNameOrUrl_Throws()
    {
        using var dir = new TempDir();
        var reg = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "r.toml"));
        var act1 = () => reg.AddSource(new ProviderSource { Name = "", Url = "https://x" });
        act1.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RemoveSource_RemovesByNameCaseInsensitive()
    {
        using var dir = new TempDir();
        var reg = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "r.toml"));
        reg.AddSource(new ProviderSource { Name = "Official", Url = "https://x" });

        reg.RemoveSource("official").Should().BeTrue();
        reg.Sources.Should().BeEmpty();
        reg.RemoveSource("official").Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_MalformedFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("registries.toml", "this is not valid toml {{{{");
        var reg = new ProviderSourceRegistry(path);
        await reg.LoadAsync();
        reg.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_SkipsEntriesMissingRequiredFields()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("registries.toml", """
            [[source]]
            name = "good"
            url = "https://x"

            [[source]]
            name = ""

            [[source]]
            url = "https://y"
            """);
        var reg = new ProviderSourceRegistry(path);
        await reg.LoadAsync();
        reg.Sources.Should().ContainSingle();
        reg.Sources[0].Name.Should().Be("good");
    }

    [Fact]
    public void NormalizedUrl_EnsuresTrailingSlash()
    {
        var s = new ProviderSource { Name = "x", Url = "https://example.com/v1" };
        s.NormalizedUrl.Should().Be("https://example.com/v1/");
        var s2 = new ProviderSource { Name = "y", Url = "https://example.com/v1/" };
        s2.NormalizedUrl.Should().Be("https://example.com/v1/");
    }

    [Fact]
    public void ResolveAuth_EnvPrefix_ReadsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("OPENSHELL_TEST_TOKEN", "abc123");
        try
        {
            RegistryClient.ResolveAuth("env:OPENSHELL_TEST_TOKEN").Should().Be("abc123");
        }
        finally { Environment.SetEnvironmentVariable("OPENSHELL_TEST_TOKEN", null); }
    }

    [Fact]
    public void ResolveAuth_LiteralValue_ReturnedAsIs()
    {
        RegistryClient.ResolveAuth("literal-token").Should().Be("literal-token");
        RegistryClient.ResolveAuth(null).Should().BeNull();
    }
}
