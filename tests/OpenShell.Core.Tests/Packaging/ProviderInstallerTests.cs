using FluentAssertions;
using NSubstitute;
using OpenShell.Packaging;
using OpenShell.Packaging.Installation;
using OpenShell.Packaging.Registry;
using OpenShell.Packaging.Signing;
using OpenShell.Providers;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Packaging;

/// <summary>
/// ADR-0039 §6 / §7: ProviderInstaller 单测。覆盖 dry-run / ListInstalled / 卸载等不依赖真实 HTTP 的路径。
/// 真实 HTTP 调用通过 NSubstitute mock <see cref="RegistryClient"/> 与 <see cref="ProviderSourceRegistry"/>。
/// 所有文件系统操作隔离在 <see cref="TempDir"/> 内 (通过 ProviderInstaller 的 providersDir 覆盖参数)。
/// </summary>
public class ProviderInstallerTests
{
    [Fact]
    public void ListInstalled_EmptyProvidersDir_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var installer = CreateInstaller(dir);
        installer.ListInstalled().Should().BeEmpty();
    }

    [Fact]
    public void ListInstalled_ReturnsInstalledVersions()
    {
        using var dir = new TempDir();
        var providersDir = dir.CreateDirectory("providers");
        Directory.CreateDirectory(Path.Combine(providersDir, "fake-prov", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(providersDir, "fake-prov", "1.1.0"));

        var installer = CreateInstaller(dir, providersDir: providersDir);
        var list = installer.ListInstalled();
        list.Should().ContainSingle();
        list[0].Name.Should().Be("fake-prov");
        list[0].Versions.Should().Contain(new[] { "1.0.0", "1.1.0" });
        // 无 current 链接, 取最高版本。
        list[0].CurrentVersion.Should().Be("1.1.0");
    }

    [Fact]
    public async Task InstallAsync_NoSourcesRegistered_Throws()
    {
        using var dir = new TempDir();
        var installer = CreateInstaller(dir);
        var act = async () => await installer.InstallAsync("nonexistent");
        (await act.Should().ThrowAsync<OspPackageException>())
            .WithMessage("*No provider source*");
    }

    [Fact]
    public async Task InstallAsync_NotFoundInSources_Throws()
    {
        using var dir = new TempDir();
        var (installer, sources, client) = CreateInstallerWithDeps(dir);
        sources.AddSource(new ProviderSource { Name = "official", Url = "https://x", Trusted = true });
        client.GetPackageAsync(Arg.Any<ProviderSource>(), "nonexistent", Arg.Any<CancellationToken>())
              .Returns((PackageInfo?)null);

        var act = async () => await installer.InstallAsync("nonexistent");
        (await act.Should().ThrowAsync<OspPackageException>())
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task InstallAsync_DryRun_ReturnsResultWithoutDownload()
    {
        using var dir = new TempDir();
        var (installer, sources, client) = CreateInstallerWithDeps(dir);
        sources.AddSource(new ProviderSource { Name = "official", Url = "https://x", Trusted = true });
        client.GetPackageAsync(Arg.Any<ProviderSource>(), "s3", Arg.Any<CancellationToken>())
              .Returns(new PackageInfo
              {
                  Name = "s3",
                  Latest = "1.2.0",
                  Versions = new[] { new PackageVersionInfo { Version = "1.2.0" } },
              });

        var result = await installer.InstallAsync("s3", dryRun: true);

        result.DryRun.Should().BeTrue();
        result.Version.Should().Be("1.2.0");
        result.Source.Should().Be("official");
        result.InstallPath.Should().BeNull();
        result.Summary.Should().Contain("Dry-run");
        // DryRun 不应触发下载。
        await client.DidNotReceive().DownloadPackageAsync(
            Arg.Any<ProviderSource>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallAsync_NotInstalled_ReturnsFalse()
    {
        using var dir = new TempDir();
        var installer = CreateInstaller(dir);
        var ok = await installer.UninstallAsync("never-installed");
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task UninstallAsync_RemovesDirectoryAndPluginsConfigEntry()
    {
        using var dir = new TempDir();
        var providersDir = dir.CreateDirectory("providers");
        Directory.CreateDirectory(Path.Combine(providersDir, "to-remove", "1.0.0"));
        var pluginsConfig = new PluginsConfig(Path.Combine(dir.FullPath, "plugins.config.toml"));
        pluginsConfig.Upsert(new ProviderEntry { Name = "to-remove", Enabled = true });
        await pluginsConfig.SaveAsync();

        var sources = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "r.toml"));
        var client = Substitute.For<RegistryClient>();
        var installer = new ProviderInstaller(sources, client, new NullSignatureVerifier(), pluginsConfig, providersDir: providersDir);

        var ok = await installer.UninstallAsync("to-remove");

        ok.Should().BeTrue();
        Directory.Exists(Path.Combine(providersDir, "to-remove")).Should().BeFalse();
        await pluginsConfig.LoadAsync();
        pluginsConfig.TryGet("to-remove").Should().BeNull();
    }

    [Fact]
    public async Task InstallAsync_WhenConfigCommitFails_RestoresPreviousCurrentAndVersion()
    {
        using var dir = new TempDir();
        var providersDir = dir.CreateDirectory("providers");
        var providerRoot = Path.Combine(providersDir, "transaction-provider");
        var oldVersion = Path.Combine(providerRoot, "1.0.0");
        Directory.CreateDirectory(oldVersion);
        File.WriteAllText(Path.Combine(oldVersion, "old.txt"), "old");
        var oldCurrent = Path.Combine(providerRoot, "current");
        Directory.CreateDirectory(oldCurrent);
        File.WriteAllText(Path.Combine(oldCurrent, "old.txt"), "old");

        // 把配置路径设为已有目录, 让提交阶段稳定失败, 以验证回滚而非依赖权限环境。
        var invalidConfigPath = dir.CreateDirectory("plugins.config.toml");
        var pluginsConfig = new PluginsConfig(invalidConfigPath);
        pluginsConfig.Upsert(new ProviderEntry { Name = "transaction-provider", Enabled = true });
        var sources = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "registries.toml"));
        var client = Substitute.For<RegistryClient>();
        sources.AddSource(new ProviderSource { Name = "official", Url = "https://x", Trusted = true });

        var manifest = new ProviderManifest
        {
            Name = "transaction-provider",
            Version = "2.0.0",
            RequiredApiVersion = "1.0.0",
        };
        var payload = dir.GetFullPath("provider.dll");
        File.WriteAllText(payload, "test payload");
        var packagePath = await OspPackage.CreateAsync(manifest, new[] { payload }, dir.FullPath);
        client.GetPackageAsync(Arg.Any<ProviderSource>(), "transaction-provider", Arg.Any<CancellationToken>())
            .Returns(new PackageInfo
            {
                Name = "transaction-provider",
                Latest = "2.0.0",
                Versions = new[] { new PackageVersionInfo { Version = "2.0.0" } },
            });
        client.DownloadPackageAsync(
                Arg.Any<ProviderSource>(),
                "transaction-provider",
                "2.0.0",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var destination = callInfo.ArgAt<string>(3);
                File.Copy(packagePath, destination, overwrite: true);
                return Task.FromResult(destination);
            });

        var installer = new ProviderInstaller(
            sources, client, new NullSignatureVerifier(), pluginsConfig, providersDir: providersDir);

        var act = async () => await installer.InstallAsync("transaction-provider", "2.0.0");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        Directory.Exists(Path.Combine(providerRoot, "2.0.0")).Should().BeFalse();
        File.ReadAllText(Path.Combine(oldVersion, "old.txt")).Should().Be("old");
        File.ReadAllText(Path.Combine(oldCurrent, "old.txt")).Should().Be("old");
    }

    private static ProviderInstaller CreateInstaller(TempDir dir, string? providersDir = null)
    {
        var sources = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "registries.toml"));
        var client = Substitute.For<RegistryClient>();
        var pluginsConfig = new PluginsConfig(Path.Combine(dir.FullPath, "plugins.config.toml"));
        return new ProviderInstaller(sources, client, new NullSignatureVerifier(), pluginsConfig, providersDir: providersDir);
    }

    private static (ProviderInstaller Installer, ProviderSourceRegistry Sources, RegistryClient Client) CreateInstallerWithDeps(TempDir dir)
    {
        var sources = new ProviderSourceRegistry(Path.Combine(dir.FullPath, "registries.toml"));
        var client = Substitute.For<RegistryClient>();
        var pluginsConfig = new PluginsConfig(Path.Combine(dir.FullPath, "plugins.config.toml"));
        var installer = new ProviderInstaller(sources, client, new NullSignatureVerifier(), pluginsConfig, providersDir: dir.CreateDirectory("providers"));
        return (installer, sources, client);
    }
}
