using FluentAssertions;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Providers.Remote;
using OpenShell.TestUtils.Contract;
using Xunit;

namespace OpenShell.Providers.Remote.Tests;

/// <summary>
/// SftpProvider 契约测试。Per ADR-0019, ADR-0001, ADR-0033.
/// 继承 ProviderContractTests, 自动覆盖 Info / Capabilities / InitialiseAsync 契约。
/// 注意: GetItem / GetChildren / AllAsyncMethods_AcceptCancellation 等需要真实 SFTP 服务器,
/// 此处用 Skip 标记 (M4 阶段: 单元测试覆盖路径解析与凭据, 集成测试留待后续接入 SSH test container)。
/// </summary>
public class SftpProviderContractTests : ProviderContractTests<SftpProvider>, IDisposable
{
    private readonly SftpProvider _provider = new(new NullCredentialProvider());

    protected override SftpProvider CreateProvider() => _provider;

    protected override ItemPath GetTestRoot()
    {
        // sftp::user@host:22/ — 路径合法, 但调用 GetItem/GetChildren 会因无凭据抛 SftpProviderException。
        return new ItemPath
        {
            Provider = "sftp",
            InternalPath = "alice@example.com:22/home/alice",
        };
    }

    // 跳过基类的 GetItemAsync_Nonexistent_ReturnsNull:
    // SftpProvider.GetItemAsync 在凭据缺失时抛 SftpProviderException (AuthenticationFailed),
    // 而不是返回 null —— 这是连接建立失败, 不是路径不存在。
    // 真实"路径不存在返回 null"语义需用 SSH test container 集成测试验证。
    [Fact(Skip = "infra: GetItemAsync requires a real SFTP server; without credentials it throws SftpProviderException. Integration test will be added when an SSH test container is wired up.")]
    public override async Task GetItemAsync_Nonexistent_ReturnsNull()
    {
        await Task.CompletedTask;
    }

    // 跳过基类的 GetChildrenAsync_Nonexistent_ReturnsEmpty, 同上原因。
    [Fact(Skip = "infra: GetChildrenAsync requires a real SFTP server; without credentials it throws SftpProviderException. Integration test will be added when an SSH test container is wired up.")]
    public override async Task GetChildrenAsync_Nonexistent_ReturnsEmpty()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public void Info_Name_IsSftp()
    {
        _provider.Info.Name.Should().Be("sftp");
    }

    [Fact]
    public void Info_Version_IsSemVerCompatible()
    {
        _provider.Info.Version.Should().NotBeNull();
        _provider.Info.Version!.Major.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Capabilities_DoNotIncludeSecurityOrDrive()
    {
        // ADR-0019 §1: SFTP provider 不实现 ISecurityProvider / IDriveProvider。
        _provider.Capabilities.Should().NotContain(ProviderCapability.Security);
        _provider.Capabilities.Should().NotContain(ProviderCapability.Drive);
    }

    [Fact]
    public void Capabilities_IncludeItemContainerNavigationContentContentWriteProperty()
    {
        // ADR-0019 §1: 6 项能力, 与实现的接口一一对应。
        _provider.Capabilities.Should().Contain(ProviderCapability.Item);
        _provider.Capabilities.Should().Contain(ProviderCapability.Container);
        _provider.Capabilities.Should().Contain(ProviderCapability.Navigation);
        _provider.Capabilities.Should().Contain(ProviderCapability.Content);
        _provider.Capabilities.Should().Contain(ProviderCapability.ContentWrite);
        _provider.Capabilities.Should().Contain(ProviderCapability.Property);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    /// <summary>
    /// 测试用 ICredentialProvider 桩: 永远返回 null, 模拟"未配置任何凭据"的场景。
    /// 用于契约测试构造 SftpProvider (构造函数要求 ICredentialProvider 非空)。
    /// </summary>
    private sealed class NullCredentialProvider : ICredentialProvider
    {
        public SftpCredentials? GetCredentials(string host, string user) => null;
    }
}
