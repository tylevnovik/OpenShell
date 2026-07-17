using FluentAssertions;
using OpenShell.Providers;
using System.Text.Json;
using Xunit;

namespace OpenShell.Core.Tests.Providers;

/// <summary>
/// ADR-0038: Provider API 版本与废弃策略单测。
/// </summary>
public class ApiVersioningTests
{
    [Fact]
    public void ProviderApiVersion_Current_Is_1_0_0()
    {
        ProviderApiVersion.Current.Should().Be(new Version(1, 0, 0));
    }

    [Fact]
    public void ProviderInfo_Defaults_RequiredApiVersion_ToCurrent()
    {
        var info = new ProviderInfo { Name = "fs", Version = new Version(1, 0, 0) };
        info.RequiredApiVersion.Should().Be(ProviderApiVersion.Current);
        info.ApiStability.Should().Be(ProviderApiStability.Stable);
    }

    [Fact]
    public void ApiCompatibilityChecker_Verify_MatchingMajor_ReturnsTrue()
    {
        var info = new ProviderInfo
        {
            Name = "fs",
            Version = new Version(1, 2, 0),
            RequiredApiVersion = new Version(1, 0, 0),
        };
        ApiCompatibilityChecker.Verify(info).Should().BeTrue();
    }

    [Fact]
    public void ApiCompatibilityChecker_Verify_HigherMajor_ThrowsMismatch()
    {
        var info = new ProviderInfo
        {
            Name = "s3",
            Version = new Version(0, 9, 0),
            RequiredApiVersion = new Version(2, 0, 0),
        };
        var act = () => ApiCompatibilityChecker.Verify(info);
        var ex = act.Should().Throw<ApiMismatchException>().Which;
        ex.HostApiVersion.Should().Be(ProviderApiVersion.Current);
        ex.RequiredApiVersion.Should().Be(new Version(2, 0, 0));
        ex.Remediation.Should().Contain("升级");
        ex.ProviderInfo.Name.Should().Be("s3");
    }

    [Fact]
    public void ApiCompatibilityChecker_Verify_OlderMajor_ThrowsMismatch()
    {
        var info = new ProviderInfo
        {
            Name = "legacy",
            Version = new Version(0, 1, 0),
            RequiredApiVersion = new Version(0, 9, 0),
        };
        var act = () => ApiCompatibilityChecker.Verify(info);
        var ex = act.Should().Throw<ApiMismatchException>().Which;
        ex.Remediation.Should().Contain("升级");
    }

    [Fact]
    public void ApiCompatibilityChecker_Verify_NullInfo_Throws()
    {
        var act = () => ApiCompatibilityChecker.Verify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProviderApiAttribute_SinceVersion_ParsesToVersion()
    {
        var attr = new ProviderApiAttribute { SinceVersion = "1.5.0" };
        attr.ParsedSince().Should().Be(new Version(1, 5, 0));
    }

    [Fact]
    public void ProviderApiAttribute_InvalidVersion_ParsesToNull()
    {
        var attr = new ProviderApiAttribute { SinceVersion = "not-a-version" };
        attr.ParsedSince().Should().BeNull();
    }

    [Fact]
    public void ProviderApiAttribute_NullVersion_ParsesToNull()
    {
        var attr = new ProviderApiAttribute();
        attr.ParsedSince().Should().BeNull();
        attr.ParsedDeprecatedSince().Should().BeNull();
        attr.ParsedRemovedIn().Should().BeNull();
    }

    [Fact]
    public void CapabilityInterfaces_AllHaveProviderApiAttribute()
    {
        // Per ADR-0038 §3: all capability interfaces should be annotated.
        typeof(IProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(IItemProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(IContainerProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(INavigationProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(IContentProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(IPropertyProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(ISecurityProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
        typeof(IDriveProvider).GetCustomAttributes(typeof(ProviderApiAttribute), false)
            .Should().NotBeEmpty();
    }

    [Fact]
    public void ProviderManifest_Parse_ValidJson_ReturnsManifest()
    {
        var json = """
        {
          "name": "OpenShell.Providers.S3",
          "displayName": "AWS S3 Provider",
          "version": "1.2.0",
          "requiredApiVersion": "1.0.0",
          "apiStability": "Stable",
          "authors": ["jane@example.com"],
          "capabilities": ["Item", "Container"],
          "dependencies": [
            { "name": "OpenShell.Providers.Remote", "version": ">= 1.0.0" }
          ]
        }
        """;
        var manifest = ProviderManifest.Parse(json);
        manifest.Name.Should().Be("OpenShell.Providers.S3");
        manifest.Version.Should().Be("1.2.0");
        manifest.RequiredApiVersion.Should().Be("1.0.0");
        manifest.ApiStability.Should().Be(ProviderApiStability.Stable);
        manifest.Authors.Should().Contain("jane@example.com");
        manifest.Capabilities.Should().Contain("Item");
        manifest.Dependencies.Should().HaveCount(1);
        manifest.Dependencies[0].Name.Should().Be("OpenShell.Providers.Remote");
    }

    [Fact]
    public void ProviderManifest_Parse_MissingName_Throws()
    {
        var json = """{ "version": "1.0.0", "requiredApiVersion": "1.0.0" }""";
        var act = () => ProviderManifest.Parse(json);
        act.Should().Throw<ProviderManifestException>()
            .WithMessage("*'name'*");
    }

    [Fact]
    public void ProviderManifest_Parse_MissingRequiredApiVersion_Throws()
    {
        var json = """{ "name": "x", "version": "1.0.0" }""";
        var act = () => ProviderManifest.Parse(json);
        act.Should().Throw<ProviderManifestException>()
            .WithMessage("*'requiredApiVersion'*");
    }

    [Fact]
    public void ProviderManifest_Parse_InvalidVersion_Throws()
    {
        var json = """{ "name": "x", "version": "not-semver", "requiredApiVersion": "1.0.0" }""";
        var act = () => ProviderManifest.Parse(json);
        act.Should().Throw<ProviderManifestException>()
            .WithMessage("*not a valid SemVer*");
    }

    [Fact]
    public void ProviderManifest_ToProviderInfo_MapsFields()
    {
        var json = """
        {
          "name": "s3",
          "version": "1.2.0",
          "requiredApiVersion": "1.0.0",
          "apiStability": "Preview",
          "authors": ["jane"],
          "description": "S3 provider"
        }
        """;
        var manifest = ProviderManifest.Parse(json);
        var info = manifest.ToProviderInfo();
        info.Name.Should().Be("s3");
        info.Version.Should().Be(new Version(1, 2, 0));
        info.RequiredApiVersion.Should().Be(new Version(1, 0, 0));
        info.ApiStability.Should().Be(ProviderApiStability.Preview);
        info.Description.Should().Be("S3 provider");
        info.Author.Should().Be("jane");
    }

    [Fact]
    public void ApiMismatchException_Message_ContainsAllFields()
    {
        var info = new ProviderInfo
        {
            Name = "legacy",
            Version = new Version(1, 0, 0),
            RequiredApiVersion = new Version(2, 0, 0),
        };
        var ex = new ApiMismatchException(info, new Version(1, 0, 0), new Version(2, 0, 0), "升级 OpenShell");
        ex.Message.Should().Contain("legacy");
        ex.Message.Should().Contain("2.0.0");
        ex.Message.Should().Contain("1.0.0");
        ex.Message.Should().Contain("升级 OpenShell");
    }
}
