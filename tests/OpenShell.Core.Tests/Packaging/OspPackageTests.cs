using FluentAssertions;
using OpenShell.Packaging;
using OpenShell.Providers;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Packaging;

/// <summary>
/// ADR-0039 §1 / §10: .osp 包格式单测。打包/解包/manifest 读写。
/// </summary>
public class OspPackageTests
{
    [Fact]
    public async Task CreateAsync_WritesManifestAndFilesToZip()
    {
        using var dir = new TempDir();
        // 准备一个 manifest + 一个伪 DLL。
        var manifest = new ProviderManifest
        {
            Name = "OpenShell.Providers.S3",
            Version = "1.2.0",
            RequiredApiVersion = "1.0.0",
            ApiStability = ProviderApiStability.Stable,
        };
        var dllPath = dir.CreateFile("MyProvider.dll", "fake-il");
        var outDir = dir.CreateDirectory("out");

        var pkgPath = await OspPackage.CreateAsync(manifest, new[] { dllPath }, outDir);

        pkgPath.Should().EndWith("OpenShell.Providers.S3-1.2.0.osp");
        File.Exists(pkgPath).Should().BeTrue();
    }

    [Fact]
    public async Task OpenAsync_ReadsBackManifest()
    {
        using var dir = new TempDir();
        var manifest = new ProviderManifest
        {
            Name = "test",
            Version = "0.1.0",
            RequiredApiVersion = "1.0.0",
        };
        var dllPath = dir.CreateFile("Test.dll", "il");
        var pkgPath = await OspPackage.CreateAsync(manifest, new[] { dllPath }, dir.CreateDirectory("out"));

        await using var pkg = await OspPackage.OpenAsync(pkgPath);
        var readBack = await pkg.ReadManifestAsync();
        readBack.Name.Should().Be("test");
        readBack.Version.Should().Be("0.1.0");
        readBack.RequiredApiVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task OpenAsync_MissingManifest_Throws()
    {
        using var dir = new TempDir();
        // 构造一个不含 openshell.provider.json 的 zip。
        var zipPath = Path.Combine(dir.FullPath, "bad.osp");
        using (var za = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("foo.dll");
            using var es = entry.Open();
            es.Write(new byte[] { 1, 2, 3 }, 0, 3);
        }

        var act = async () => await OspPackage.OpenAsync(zipPath);
        (await act.Should().ThrowAsync<OspPackageException>())
            .WithMessage("*missing*openshell.provider.json*");
    }

    [Fact]
    public async Task OpenAsync_FileNotFound_Throws()
    {
        using var dir = new TempDir();
        var act = async () => await OspPackage.OpenAsync(Path.Combine(dir.FullPath, "nope.osp"));
        (await act.Should().ThrowAsync<OspPackageException>())
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ExtractTo_ExtractsAllEntries()
    {
        using var dir = new TempDir();
        var manifest = new ProviderManifest
        {
            Name = "ext-test",
            Version = "1.0.0",
            RequiredApiVersion = "1.0.0",
        };
        var dllPath = dir.CreateFile("Ext.dll", "il-bytes");
        var pkgPath = await OspPackage.CreateAsync(manifest, new[] { dllPath }, dir.CreateDirectory("out"));

        await using var pkg = await OspPackage.OpenAsync(pkgPath);
        var extractDir = Path.Combine(dir.FullPath, "extracted");
        await pkg.ExtractToAsync(extractDir);

        File.Exists(Path.Combine(extractDir, "openshell.provider.json")).Should().BeTrue();
        File.Exists(Path.Combine(extractDir, "Ext.dll")).Should().BeTrue();
        File.ReadAllText(Path.Combine(extractDir, "Ext.dll")).Should().Be("il-bytes");
    }

    [Fact]
    public async Task ReadSignature_OnUnsignedPackage_ReturnsNulls()
    {
        using var dir = new TempDir();
        var manifest = new ProviderManifest { Name = "unsigned", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var dllPath = dir.CreateFile("U.dll", "x");
        var pkgPath = await OspPackage.CreateAsync(manifest, new[] { dllPath }, dir.CreateDirectory("out"));

        await using var pkg = await OspPackage.OpenAsync(pkgPath);
        var (sig, pub) = pkg.ReadSignature();
        sig.Should().BeNull();
        pub.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_InvalidManifest_Throws()
    {
        using var dir = new TempDir();
        // manifest 缺 requiredApiVersion, Validate 会抛。
        var manifest = new ProviderManifest { Name = "bad", Version = "1.0.0", RequiredApiVersion = "" };
        var dllPath = dir.CreateFile("bad.dll", "x");

        var act = async () => await OspPackage.CreateAsync(manifest, new[] { dllPath }, dir.CreateDirectory("out"));
        await act.Should().ThrowAsync<ProviderManifestException>();
    }
}
