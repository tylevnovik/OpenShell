using FluentAssertions;
using OpenShell.Packaging;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Packaging;

/// <summary>
/// ADR-0039 §6: <c>plugins.config.toml</c> 配置单测。读写 / Upsert / Remove。
/// </summary>
public class PluginsConfigTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var cfg = new PluginsConfig(Path.Combine(dir.FullPath, "plugins.config.toml"));
        await cfg.LoadAsync();
        cfg.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLoad_RoundTripsEntries()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "plugins.config.toml");
        var cfg = new PluginsConfig(path);
        cfg.Upsert(new ProviderEntry
        {
            Name = "s3",
            Enabled = true,
            LoadOrder = 10,
            AutoUpdate = true,
            Config = new Dictionary<string, object?> { ["Region"] = "us-east-1" },
        });
        cfg.Upsert(new ProviderEntry { Name = "reg", Enabled = false, LoadOrder = 5 });

        await cfg.SaveAsync();

        var cfg2 = new PluginsConfig(path);
        await cfg2.LoadAsync();
        cfg2.Providers.Should().HaveCount(2);
        // 排序: LoadOrder 升序 → reg(5) 在 s3(10) 前。
        cfg2.Providers[0].Name.Should().Be("reg");
        cfg2.Providers[1].Name.Should().Be("s3");
        cfg2.Providers[1].AutoUpdate.Should().BeTrue();
        cfg2.Providers[1].Config["Region"].Should().Be("us-east-1");
    }

    [Fact]
    public void Upsert_UpdatesExistingByName()
    {
        using var dir = new TempDir();
        var cfg = new PluginsConfig(Path.Combine(dir.FullPath, "p.toml"));
        cfg.Upsert(new ProviderEntry { Name = "s3", Enabled = true, LoadOrder = 10 });
        cfg.Upsert(new ProviderEntry { Name = "s3", Enabled = false, LoadOrder = 99 });

        cfg.Providers.Should().ContainSingle();
        cfg.Providers[0].Enabled.Should().BeFalse();
        cfg.Providers[0].LoadOrder.Should().Be(99);
    }

    [Fact]
    public void Upsert_EmptyName_Throws()
    {
        using var dir = new TempDir();
        var cfg = new PluginsConfig(Path.Combine(dir.FullPath, "p.toml"));
        var act = () => cfg.Upsert(new ProviderEntry { Name = "" });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Remove_DeletesByNameCaseInsensitive()
    {
        using var dir = new TempDir();
        var cfg = new PluginsConfig(Path.Combine(dir.FullPath, "p.toml"));
        cfg.Upsert(new ProviderEntry { Name = "S3" });
        cfg.Remove("s3").Should().BeTrue();
        cfg.Providers.Should().BeEmpty();
        cfg.Remove("s3").Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_MalformedFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("plugins.config.toml", "garbage {{{");
        var cfg = new PluginsConfig(path);
        await cfg.LoadAsync();
        cfg.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_SkipsEntriesMissingName()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("plugins.config.toml", """
            [[provider]]
            name = "good"
            enabled = true

            [[provider]]
            enabled = false
            """);
        var cfg = new PluginsConfig(path);
        await cfg.LoadAsync();
        cfg.Providers.Should().ContainSingle();
        cfg.Providers[0].Name.Should().Be("good");
        cfg.Providers[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void TryGet_FindsCaseInsensitive()
    {
        using var dir = new TempDir();
        var cfg = new PluginsConfig(Path.Combine(dir.FullPath, "p.toml"));
        cfg.Upsert(new ProviderEntry { Name = "S3" });
        cfg.TryGet("s3").Should().NotBeNull();
        cfg.TryGet("S3").Should().NotBeNull();
        cfg.TryGet("nope").Should().BeNull();
    }
}
