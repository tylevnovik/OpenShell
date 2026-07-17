using FluentAssertions;
using OpenShell.Providers.Remote;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Providers.Remote.Tests;

/// <summary>
/// InMemoryCredentialProvider 单测。Per ADR-0019 §3.
/// 验证 Set/Get/Remove/List 操作 + 持久化 (文件加载/保存/原子替换) + 边界条件。
/// 用 TempDir 隔离凭据文件, 避免污染用户 home。
/// </summary>
public class InMemoryCredentialProviderTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    private string CredFilePath => System.IO.Path.Combine(_tempDir.FullPath, "sftp-creds.json");

    private InMemoryCredentialProvider CreateProvider() => new(CredFilePath);

    // ---- GetCredentials ----

    [Fact]
    public void GetCredentials_EmptyStore_ReturnsNull()
    {
        var provider = CreateProvider();

        var cred = provider.GetCredentials("example.com", "alice");

        cred.Should().BeNull();
    }

    [Fact]
    public void GetCredentials_AfterSet_ReturnsCredential()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "s3cret",
        });

        var cred = provider.GetCredentials("example.com", "alice");

        cred.Should().NotBeNull();
        cred!.Host.Should().Be("example.com");
        cred.User.Should().Be("alice");
        cred.Password.Should().Be("s3cret");
        cred.Port.Should().Be(22);
    }

    [Fact]
    public void GetCredentials_CaseInsensitive_MatchesHostAndUser()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "Example.COM",
            User = "Alice",
            Password = "pw",
        });

        // 查询用不同大小写, 应回命中 (ADR-0019 §3: host/user 不区分大小写匹配)。
        provider.GetCredentials("example.com", "alice").Should().NotBeNull();
        provider.GetCredentials("EXAMPLE.COM", "ALICE").Should().NotBeNull();
    }

    [Fact]
    public void GetCredentials_DifferentUser_ReturnsNull()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "pw",
        });

        provider.GetCredentials("example.com", "bob").Should().BeNull();
    }

    [Fact]
    public void GetCredentials_NullArguments_Throws()
    {
        var provider = CreateProvider();

        Action act1 = () => provider.GetCredentials(null!, "alice");
        Action act2 = () => provider.GetCredentials("example.com", null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    // ---- SetCredentials (覆盖语义) ----

    [Fact]
    public void SetCredentials_SameHostSameUser_OverwritesExisting()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "old-pw",
        });

        provider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "new-pw",
            Port = 2222,
        });

        var cred = provider.GetCredentials("example.com", "alice");
        cred!.Password.Should().Be("new-pw");
        cred.Port.Should().Be(2222);

        // 同 host+user 只有一条记录。
        provider.ListCredentials().Should().HaveCount(1);
    }

    [Fact]
    public void SetCredentials_SameHostDifferentUser_KeepsBoth()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "h", User = "alice", Password = "1" });
        provider.SetCredentials(new SftpCredentials { Host = "h", User = "bob", Password = "2" });

        var all = provider.ListCredentials();
        all.Should().HaveCount(2);
    }

    [Fact]
    public void SetCredentials_DifferentHosts_KeepsAll()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "h1", User = "alice", Password = "1" });
        provider.SetCredentials(new SftpCredentials { Host = "h2", User = "alice", Password = "2" });

        var all = provider.ListCredentials();
        all.Should().HaveCount(2);
    }

    [Fact]
    public void SetCredentials_NullArgument_Throws()
    {
        var provider = CreateProvider();

        Action act = () => provider.SetCredentials(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetCredentials_PersistsToFile()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "s3cret",
            Port = 2222,
        });

        // 文件存在且包含 password。
        File.Exists(CredFilePath).Should().BeTrue();
        var json = File.ReadAllText(CredFilePath);
        json.Should().Contain("example.com");
        json.Should().Contain("alice");
        json.Should().Contain("s3cret");
        json.Should().Contain("2222");
    }

    // ---- ListCredentials ----

    [Fact]
    public void ListCredentials_EmptyStore_ReturnsEmpty()
    {
        var provider = CreateProvider();

        var all = provider.ListCredentials();

        all.Should().BeEmpty();
    }

    [Fact]
    public void ListCredentials_AfterMultipleSets_ReturnsAllSortedByHostThenUser()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "zeta.com", User = "alice", Password = "1" });
        provider.SetCredentials(new SftpCredentials { Host = "alpha.com", User = "bob", Password = "2" });
        provider.SetCredentials(new SftpCredentials { Host = "alpha.com", User = "alice", Password = "3" });

        var all = provider.ListCredentials();

        all.Should().HaveCount(3);
        all[0].Host.Should().Be("alpha.com");
        all[0].User.Should().Be("alice");
        all[1].Host.Should().Be("alpha.com");
        all[1].User.Should().Be("bob");
        all[2].Host.Should().Be("zeta.com");
    }

    [Fact]
    public void ListCredentials_WithHostFilter_ReturnsMatchingOnly()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "alpha.com", User = "alice", Password = "1" });
        provider.SetCredentials(new SftpCredentials { Host = "beta.com", User = "bob", Password = "2" });

        var filtered = provider.ListCredentials("alpha.com");

        filtered.Should().HaveCount(1);
        filtered[0].Host.Should().Be("alpha.com");
    }

    [Fact]
    public void ListCredentials_PasswordMasked_DoesNotReturnPlaintext()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "h",
            User = "alice",
            Password = "super-secret-pw-123",
        });

        var all = provider.ListCredentials();

        all.Should().HaveCount(1);
        // ListCredentials 把 password 替换为 "****", 不返回明文。
        all[0].Password.Should().Be("****");
        all[0].Password.Should().NotBe("super-secret-pw-123");
    }

    [Fact]
    public void ListCredentials_NullPassword_ShowsAsNull()
    {
        var provider = CreateProvider();
        // 只有 private key, 没有 password。
        provider.SetCredentials(new SftpCredentials
        {
            Host = "h",
            User = "alice",
            PrivateKeyPath = "/home/alice/.ssh/id_rsa",
        });

        var all = provider.ListCredentials();

        all.Should().HaveCount(1);
        all[0].Password.Should().BeNull();
    }

    // ---- RemoveCredentials ----

    [Fact]
    public void RemoveCredentials_ByHostAndUser_RemovesMatchingEntry()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "h", User = "alice", Password = "1" });
        provider.SetCredentials(new SftpCredentials { Host = "h", User = "bob", Password = "2" });

        var removed = provider.RemoveCredentials("h", "alice");

        removed.Should().BeTrue();
        provider.ListCredentials().Should().HaveCount(1);
        provider.GetCredentials("h", "alice").Should().BeNull();
        provider.GetCredentials("h", "bob").Should().NotBeNull();
    }

    [Fact]
    public void RemoveCredentials_ByHostOnly_RemovesAllForHost()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "h1", User = "alice", Password = "1" });
        provider.SetCredentials(new SftpCredentials { Host = "h1", User = "bob", Password = "2" });
        provider.SetCredentials(new SftpCredentials { Host = "h2", User = "alice", Password = "3" });

        var removed = provider.RemoveCredentials("h1");

        removed.Should().BeTrue();
        provider.ListCredentials().Should().HaveCount(1);
        provider.ListCredentials()[0].Host.Should().Be("h2");
    }

    [Fact]
    public void RemoveCredentials_NotFound_ReturnsFalse()
    {
        var provider = CreateProvider();

        var removed = provider.RemoveCredentials("nonexistent.com");

        removed.Should().BeFalse();
    }

    [Fact]
    public void RemoveCredentials_NullHost_Throws()
    {
        var provider = CreateProvider();

        Action act = () => provider.RemoveCredentials(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RemoveCredentials_CaseInsensitive_MatchesHostAndUser()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials { Host = "Example.COM", User = "Alice", Password = "1" });

        var removed = provider.RemoveCredentials("example.com", "alice");

        removed.Should().BeTrue();
        provider.ListCredentials().Should().BeEmpty();
    }

    // ---- 持久化 (加载/保存) ----

    [Fact]
    public void Constructor_LoadsExistingFile()
    {
        // 先用 provider1 写入凭据, 再构造新 provider (同一文件路径) 验证加载。
        var provider1 = CreateProvider();
        provider1.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "s3cret",
            Port = 2222,
            PrivateKeyPath = "/key",
        });

        var provider2 = CreateProvider();

        var cred = provider2.GetCredentials("example.com", "alice");
        cred.Should().NotBeNull();
        cred!.Password.Should().Be("s3cret");
        cred.Port.Should().Be(2222);
        cred.PrivateKeyPath.Should().Be("/key");
    }

    [Fact]
    public void Constructor_SkipsInvalidEntriesInFile()
    {
        // 写入一个无效条目 (空 Host) + 一个有效条目。
        var json = """[{"host":"","user":"alice"},{"host":"example.com","user":"bob","port":22}]""";
        File.WriteAllText(CredFilePath, json);

        var provider = CreateProvider();

        // 只加载了有效的那条。
        var all = provider.ListCredentials();
        all.Should().HaveCount(1);
        all[0].Host.Should().Be("example.com");
        all[0].User.Should().Be("bob");
    }

    [Fact]
    public void Constructor_MissingPort_DefaultsTo22()
    {
        var json = """[{"host":"h","user":"u"}]""";
        File.WriteAllText(CredFilePath, json);

        var provider = CreateProvider();

        var cred = provider.GetCredentials("h", "u");
        cred!.Port.Should().Be(22);
    }

    [Fact]
    public void Constructor_NonexistentFile_EmptyStore()
    {
        // 不创建文件, 直接构造 provider。
        var path = System.IO.Path.Combine(_tempDir.FullPath, "nonexistent.json");
        var provider = new InMemoryCredentialProvider(path);

        provider.ListCredentials().Should().BeEmpty();
    }

    [Fact]
    public void Constructor_CorruptedFile_DoesNotThrow_EmptyStore()
    {
        File.WriteAllText(CredFilePath, "this is not valid JSON");
        var provider = CreateProvider();

        // Per ADR-0019 §3: 加载失败不抛, 降级到空列表。
        provider.ListCredentials().Should().BeEmpty();
    }

    // ---- 私钥凭据 ----

    [Fact]
    public void SetCredentials_WithPrivateKey_StoresAllFields()
    {
        var provider = CreateProvider();
        provider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            PrivateKeyPath = "/home/alice/.ssh/id_rsa",
            PrivateKeyPassphrase = "passphrase-secret",
            Port = 2222,
        });

        var cred = provider.GetCredentials("example.com", "alice");

        cred.Should().NotBeNull();
        cred!.PrivateKeyPath.Should().Be("/home/alice/.ssh/id_rsa");
        cred.PrivateKeyPassphrase.Should().Be("passphrase-secret");
        cred.Port.Should().Be(2222);
    }
}
