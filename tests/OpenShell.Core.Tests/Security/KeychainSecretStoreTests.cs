#nullable enable

using FluentAssertions;
using OpenShell.Security;
using Xunit;

namespace OpenShell.Core.Tests.Security;

/// <summary>
/// IH-012: Keychain 秘密存储与默认存储工厂的合规测试。
/// Keychain 行为用可注入的进程运行器模拟 (测试环境不依赖 macOS)。
/// </summary>
public sealed class KeychainSecretStoreTests
{
    /// <summary>内存模拟的 security 工具: 按 (账户, 服务名) 存值, 用真实退出码语义。</summary>
    private sealed class FakeSecurityTool
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

        public KeychainSecretStore.ProcessResult Run(string fileName, IReadOnlyList<string> args)
        {
            fileName.Should().Be("/usr/bin/security");
            var verb = args[0];
            var service = args.SkipWhile(a => a != "-s").Skip(1).First();

            switch (verb)
            {
                case "find-generic-password":
                    return _items.TryGetValue(service, out var value)
                        ? new KeychainSecretStore.ProcessResult(0, value + "\n", "")
                        : new KeychainSecretStore.ProcessResult(KeychainSecretStore.ExitCodeItemNotFound, "", "not found");

                case "add-generic-password":
                    _items[service] = args.SkipWhile(a => a != "-w").Skip(1).First();
                    return new KeychainSecretStore.ProcessResult(0, "", "");

                case "delete-generic-password":
                    return _items.Remove(service)
                        ? new KeychainSecretStore.ProcessResult(0, "", "")
                        : new KeychainSecretStore.ProcessResult(KeychainSecretStore.ExitCodeItemNotFound, "", "not found");

                default:
                    return new KeychainSecretStore.ProcessResult(1, "", $"unknown verb {verb}");
            }
        }
    }

    [Fact]
    public void Keychain_SetGetRemove_RoundTrip()
    {
        var store = new KeychainSecretStore(new FakeSecurityTool().Run);

        store.SetSecret("sftp/host/user/password", "hunter2");
        store.GetSecret("sftp/host/user/password").Should().Be("hunter2");

        store.RemoveSecret("sftp/host/user/password");
        store.GetSecret("sftp/host/user/password").Should().BeNull();
    }

    [Fact]
    public void Keychain_GetMissing_ReturnsNull()
    {
        var store = new KeychainSecretStore(new FakeSecurityTool().Run);
        store.GetSecret("never/set").Should().BeNull();
    }

    [Fact]
    public void Keychain_RemoveMissing_IsIdempotent()
    {
        var store = new KeychainSecretStore(new FakeSecurityTool().Run);
        var act = () => store.RemoveSecret("never/set");
        act.Should().NotThrow();
    }

    [Fact]
    public void Keychain_ToolFailure_ThrowsExplicitly()
    {
        // 持久化失败不得静默: 非 0/44 退出码必须抛异常。
        var store = new KeychainSecretStore((_, _) => new KeychainSecretStore.ProcessResult(1, "", "keychain locked"));

        var act = () => store.SetSecret("k", "v");
        act.Should().Throw<InvalidOperationException>().WithMessage("*keychain locked*");

        var getAct = () => store.GetSecret("k");
        getAct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Keychain_Probe_AcceptsOnlyItemNotFound()
    {
        KeychainSecretStore.IsAvailable((_, _) =>
            new KeychainSecretStore.ProcessResult(KeychainSecretStore.ExitCodeItemNotFound, "", "")).Should().BeTrue();
        KeychainSecretStore.IsAvailable((_, _) =>
            new KeychainSecretStore.ProcessResult(25292, "", "no default keychain")).Should().BeFalse();
        KeychainSecretStore.IsAvailable((_, _) =>
            new KeychainSecretStore.ProcessResult(-1, "", "start failed")).Should().BeFalse();
    }

    [Fact]
    public void Factory_SelectsKeychain_OnlyOnMacWithWorkingSession()
    {
        using var temp = new OpenShell.TestUtils.TempDir();
        var path = temp.GetFullPath("secrets.json");

        SecretStoreFactory.CreateDefault(path, isWindows: false, isMacOS: true, keychainToolAvailable: true, () => true)
            .Should().BeOfType<KeychainSecretStore>();
        SecretStoreFactory.CreateDefault(path, isWindows: false, isMacOS: true, keychainToolAvailable: true, () => false)
            .Should().BeOfType<ProtectedFileSecretStore>("钥匙串会话不可用 (如 CI) 必须回退受保护文件");
        SecretStoreFactory.CreateDefault(path, isWindows: false, isMacOS: true, keychainToolAvailable: false, () => true)
            .Should().BeOfType<ProtectedFileSecretStore>();
        SecretStoreFactory.CreateDefault(path, isWindows: true, isMacOS: false, keychainToolAvailable: false, () => false)
            .Should().BeOfType<ProtectedFileSecretStore>("Windows 用 DPAPI 文件存储");
        SecretStoreFactory.CreateDefault(path, isWindows: false, isMacOS: false, keychainToolAvailable: false, () => false)
            .Should().BeOfType<ProtectedFileSecretStore>("Linux 按原建议允许受保护文件");
    }
}
