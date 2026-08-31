using System.Security.Cryptography;
using FluentAssertions;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using Renci.SshNet;
using Xunit;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Remote.Tests;

/// <summary>
/// IH-006: 真实 SFTP 服务器集成测试。
/// 需要外部环境变量配置 (无基础设施时自动 Skip, 不影响常规测试):
/// <list type="bullet">
///   <item><c>OPENSHELL_TEST_SFTP_HOST</c> / <c>OPENSHELL_TEST_SFTP_PORT</c> (默认 22)</item>
///   <item><c>OPENSHELL_TEST_SFTP_USER</c> / <c>OPENSHELL_TEST_SFTP_PASSWORD</c></item>
///   <item><c>OPENSHELL_TEST_SFTP_ROOT</c> (可选: 显式指定可写根目录; 缺省自动探测工作目录与 upload/uploads)</item>
/// </list>
/// CI 中由 <c>remote-integration</c> 作业提供隔离 OpenSSH 容器。
/// 主机密钥策略: 当前实现沿用 SSH.NET 默认的首次信任 (accept-all);
/// 本套件每次运行都连接全新容器密钥, 覆盖该策略路径; 密钥固定 (pinning) 另见审计文档后续任务。
/// </summary>
public class SftpIntegrationTests : IDisposable
{
    private readonly SftpProvider _provider;
    private readonly string _testDir;

    public SftpIntegrationTests()
    {
        _provider = new SftpProvider(new EnvCredentialProvider());
        using var client = CreateDirectClient();
        client.Connect();
        _testDir = EnsureWritableTestDir(client);
    }

    public void Dispose()
    {
        try
        {
            using var client = CreateDirectClient();
            client.Connect();
            DeleteRecursive(client, _testDir);
        }
        catch
        {
            // 清理失败不阻断测试结果; 容器是一次性环境。
        }
        _provider.Dispose();
    }

    [SftpIntegrationFact]
    public async Task GetItem_ReturnsItem_WhenExists_AndNull_WhenMissing()
    {
        await WriteFileAsync($"{_testDir}/hello.txt", "hello sftp"u8.ToArray());

        var item = await _provider.GetItemAsync(RemotePath($"{_testDir}/hello.txt"));
        item.Should().NotBeNull();
        item!.Name.Should().Be("hello.txt");
        item.Kind.Should().Be(ItemKind.File);
        item.Size.Should().Be(10);

        var missing = await _provider.GetItemAsync(RemotePath($"{_testDir}/does-not-exist.txt"));
        missing.Should().BeNull("路径不存在必须返回 null, 而不是抛异常");
    }

    [SftpIntegrationFact]
    public async Task GetChildren_ListsSeededFiles_AndReturnsEmpty_ForMissingDirectory()
    {
        await WriteFileAsync($"{_testDir}/a.txt", [1]);
        await WriteFileAsync($"{_testDir}/b.txt", [2]);

        var names = new List<string>();
        await foreach (var child in _provider.GetChildrenAsync(
            RemotePath(_testDir), new EnumerationOptions()))
            names.Add(child.Name);

        names.Should().Contain(new[] { "a.txt", "b.txt" });
        names.Should().NotContain(new[] { ".", ".." });

        var empty = new List<IItem>();
        await foreach (var child in _provider.GetChildrenAsync(
            RemotePath($"{_testDir}/missing-dir"), new EnumerationOptions()))
            empty.Add(child);
        empty.Should().BeEmpty("不存在的目录必须返回空枚举, 而不是抛异常");
    }

    [SftpIntegrationFact]
    public async Task CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _provider.GetItemAsync(RemotePath($"{_testDir}/x.txt"), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [SftpIntegrationFact]
    public async Task LargeFile_UploadDownload_RoundTrips()
    {
        // 8MB 随机数据: 覆盖分块/流式传输路径。
        var payload = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var remote = $"{_testDir}/large.bin";

        await WriteFileAsync(remote, payload);

        await using var read = await _provider.OpenReadAsync(RemotePath(remote));
        using var ms = new MemoryStream();
        await read.CopyToAsync(ms);
        SHA256.HashData(ms.ToArray()).Should().BeEquivalentTo(SHA256.HashData(payload),
            "下载内容必须与上传内容逐字节一致");
    }

    [SftpIntegrationFact]
    public async Task Reconnect_AfterForcedDisconnect_Succeeds()
    {
        await WriteFileAsync($"{_testDir}/reconnect.txt", "again"u8.ToArray());
        (await _provider.GetItemAsync(RemotePath($"{_testDir}/reconnect.txt"))).Should().NotBeNull();

        // IH-006: 故障注入——断开所有池化连接, 下一次操作必须透明重连。
        _provider.DisconnectPooledConnections();

        var after = await _provider.GetItemAsync(RemotePath($"{_testDir}/reconnect.txt"));
        after.Should().NotBeNull("断线后的操作应自动重连并成功");
    }

    [SftpIntegrationFact]
    public async Task WrongPassword_ThrowsAuthenticationFailed()
    {
        var badProvider = new SftpProvider(new EnvCredentialProvider(passwordOverride: "wrong-password"));
        try
        {
            var act = async () => await badProvider.GetItemAsync(RemotePath($"{_testDir}/x.txt"));
            var result = await act.Should().ThrowAsync<SftpProviderException>();
            result.Which.Category.Should().Be(ErrorCategory.AuthenticationFailed);
        }
        finally
        {
            badProvider.Dispose();
        }
    }

    // ---- helpers ----

    private static SftpClient CreateDirectClient()
        => new(SftpTestEnv.Host!, SftpTestEnv.Port, SftpTestEnv.User!, SftpTestEnv.Password!);

    private static ItemPath RemotePath(string remotePath) => new()
    {
        Provider = "sftp",
        InternalPath = $"{SftpTestEnv.User}@{SftpTestEnv.Host}:{SftpTestEnv.Port}{remotePath}",
    };

    private async Task WriteFileAsync(string remotePath, byte[] content)
    {
        await using var stream = await _provider.OpenWriteAsync(RemotePath(remotePath));
        await stream.WriteAsync(content);
    }

    /// <summary>
    /// 发现可写测试目录: 依次尝试显式配置的 Root、登录工作目录 (chroot 感知)、
    /// atmoz/sftp 惯例的 upload / uploads 子目录; 第一个能 mkdir 成功的位置胜出。
    /// CI 首跑证明硬编码 /upload 不存在于容器视图, 因此不对服务器布局做假设。
    /// </summary>
    private static string EnsureWritableTestDir(SftpClient client)
    {
        var home = (client.WorkingDirectory ?? "").TrimEnd('/');
        if (home.Length == 0) home = "/";
        var unique = "os-it-" + Guid.NewGuid().ToString("N");

        var parents = new List<string>();
        var configured = Environment.GetEnvironmentVariable("OPENSHELL_TEST_SFTP_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) parents.Add(configured.TrimEnd('/'));
        parents.Add(home);
        foreach (var sub in new[] { "upload", "uploads" })
        {
            var dir = home == "/" ? "/" + sub : home + "/" + sub;
            if (!parents.Contains(dir)) parents.Add(dir);
        }

        foreach (var parent in parents)
        {
            var dir = (parent == "/" ? "" : parent) + "/" + unique;
            try
            {
                // 子目录 (upload/uploads) 可能尚不存在; "已存在/无权限" 均不影响下一步试探。
                try { client.CreateDirectory(parent); }
                catch (Renci.SshNet.Common.SshException) { /* 继续直接试 mkdir 测试目录 */ }
                client.CreateDirectory(dir);
                return dir;
            }
            catch (Renci.SshNet.Common.SshException)
            {
                // SftpPermissionDeniedException / SftpPathNotFoundException 等都进入下一个候选。
            }
        }

        throw new InvalidOperationException(
            $"SFTP server exposes no writable test directory (tried: {string.Join(", ", parents)}).");
    }

    private static void DeleteRecursive(SftpClient client, string path)
    {
        foreach (var entry in client.ListDirectory(path))
        {
            if (entry.Name is "." or "..") continue;
            var child = $"{path}/{entry.Name}";
            if (entry.IsDirectory) DeleteRecursive(client, child);
            else client.DeleteFile(child);
        }
        client.DeleteDirectory(path);
    }

    private sealed class EnvCredentialProvider : ICredentialProvider
    {
        private readonly string? _passwordOverride;

        public EnvCredentialProvider(string? passwordOverride = null)
            => _passwordOverride = passwordOverride;

        public SftpCredentials? GetCredentials(string host, string user)
            => new()
            {
                Host = SftpTestEnv.Host!,
                User = SftpTestEnv.User!,
                Port = SftpTestEnv.Port,
                Password = _passwordOverride ?? SftpTestEnv.Password!,
            };
    }
}

/// <summary>真实 SFTP 测试环境变量读取。</summary>
public static class SftpTestEnv
{
    public static string? Host => Get("OPENSHELL_TEST_SFTP_HOST");
    public static int Port => int.TryParse(Get("OPENSHELL_TEST_SFTP_PORT"), out var p) && p > 0 ? p : 22;
    public static string? User => Get("OPENSHELL_TEST_SFTP_USER");
    public static string? Password => Get("OPENSHELL_TEST_SFTP_PASSWORD");

    public static bool IsConfigured
        => !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Password);

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>
/// 条件集成测试: 未配置 <c>OPENSHELL_TEST_SFTP_*</c> 环境变量时在发现阶段即 Skip,
/// 配置后按普通 [Fact] 执行。
/// </summary>
public sealed class SftpIntegrationFactAttribute : FactAttribute
{
    public SftpIntegrationFactAttribute()
    {
        if (!SftpTestEnv.IsConfigured)
        {
            Skip = "infra: set OPENSHELL_TEST_SFTP_HOST/USER/PASSWORD to run real SFTP integration tests "
                + "(CI wires an isolated OpenSSH container in the remote-integration job).";
        }
    }
}
