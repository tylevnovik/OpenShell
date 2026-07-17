using FluentAssertions;
using OpenShell.Paths;
using OpenShell.Providers.Remote;
using Xunit;

namespace OpenShell.Providers.Remote.Tests;

/// <summary>
/// SFTP 内部路径解析单测。Per ADR-0019 §2.
/// 路径格式: <c>user@host[:port]/path/to/file</c>。
/// 验证 user/host/port/remotePath 各字段的边界条件 (空, 默认端口 22, port 范围, IPv6 不支持等)。
/// 直接调用 internal static 方法 (InternalsVisibleTo 暴露)。
/// </summary>
public class SftpPathParsingTests
{
    // ---- TryParseInternalPath: 合法路径 ----

    [Theory]
    [InlineData("alice@example.com/home/alice", "alice", "example.com", 22, "/home/alice")]
    [InlineData("alice@example.com:22/home/alice", "alice", "example.com", 22, "/home/alice")]
    [InlineData("alice@example.com:2222/home/alice", "alice", "example.com", 2222, "/home/alice")]
    [InlineData("alice@example.com:2222/", "alice", "example.com", 2222, "/")]
    [InlineData("alice@example.com:2222", "alice", "example.com", 2222, "")]
    [InlineData("bob@10.0.0.1/var/log", "bob", "10.0.0.1", 22, "/var/log")]
    [InlineData("bob@10.0.0.1:22022/var/log", "bob", "10.0.0.1", 22022, "/var/log")]
    [InlineData("root@host/", "root", "host", 22, "/")]
    [InlineData("a@b/c", "a", "b", 22, "/c")]
    [InlineData("user@host/path/with/many/segments/file.txt", "user", "host", 22, "/path/with/many/segments/file.txt")]
    public void TryParseInternalPath_ValidPath_ReturnsTrue(
        string input, string expUser, string expHost, int expPort, string expRemote)
    {
        var ok = SftpProvider.TryParseInternalPath(input, out var result);

        ok.Should().BeTrue();
        result.user.Should().Be(expUser);
        result.host.Should().Be(expHost);
        result.port.Should().Be(expPort);
        result.remotePath.Should().Be(expRemote);
    }

    // ---- TryParseInternalPath: 非法路径 ----

    [Theory]
    [InlineData("", "empty string")]
    [InlineData("alice@", "missing host: 'alice@'")]
    [InlineData("@host/path", "missing user: '@host/path'")]
    [InlineData("@/", "both missing: '@/'")]
    [InlineData("alice@host:0/path", "port out of range (0)")]
    [InlineData("alice@host:65536/path", "port out of range (65536)")]
    [InlineData("alice@host:-1/path", "port negative")]
    [InlineData("alice@host:abc/path", "port not numeric")]
    [InlineData("alice@host:22abc/path", "port with trailing garbage")]
    [InlineData("alice@:22/path", "host missing between @ and :")]
    public void TryParseInternalPath_InvalidPath_ReturnsFalse(string input, string reason)
    {
        var ok = SftpProvider.TryParseInternalPath(input, out var result);

        ok.Should().BeFalse(
            $"because '{input}' is invalid ({reason})");
        result.Should().Be(default((string, string, int, string)));
    }

    // ---- TryParseInternalPath: 边界 (port 边界值) ----

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(65535)]
    public void TryParseInternalPath_PortBoundary_ReturnsTrue(int port)
    {
        var input = $"alice@host:{port}/path";

        var ok = SftpProvider.TryParseInternalPath(input, out var result);

        ok.Should().BeTrue();
        result.port.Should().Be(port);
    }

    // ---- TryParseInternalPath: IPv6 不支持 (M4 限制, 文档明确说明) ----

    [Fact]
    public void TryParseInternalPath_Ipv6_NotSupported_ReturnsFalse()
    {
        // IPv6 字面量如 [::1]:22 会因多个 ':' 而被当作 host:port 解析失败。
        // ADR-0019 §2 注: M4 阶段不支持 IPv6, 后续可加 [] 语法支持。
        var ok = SftpProvider.TryParseInternalPath("alice@[::1]:22/path", out _);

        // 当前实现按 IndexOf(':') 找第一个 ':' 来拆分 host 与 port,
        // 对 [::1]:22 会把 host 解析为 "[", portStr 为 "::1]:22", int.TryParse 失败 → 返回 false。
        ok.Should().BeFalse("IPv6 literal is not supported in M4 (Per ADR-0019 §2)");
    }

    // ---- ParseInternalPath: 抛 ArgumentException ----

    [Theory]
    [InlineData("")]
    [InlineData("@host/path")]
    [InlineData("alice@")]
    [InlineData("alice@host:0/path")]
    [InlineData("alice@host:abc/path")]
    public void ParseInternalPath_InvalidPath_ThrowsArgumentException(string input)
    {
        var act = () => SftpProvider.ParseInternalPath(input);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("internalPath")
            .WithMessage($"Invalid SFTP path: '{input}'*");
    }

    // ---- ParseInternalPath: 合法路径返回正确字段 ----

    [Fact]
    public void ParseInternalPath_ValidPath_ReturnsTuple()
    {
        var (user, host, port, remotePath) = SftpProvider.ParseInternalPath("alice@example.com:2222/home/alice");

        user.Should().Be("alice");
        host.Should().Be("example.com");
        port.Should().Be(2222);
        remotePath.Should().Be("/home/alice");
    }

    // ---- ParseInternalPath: 默认端口 22 ----

    [Fact]
    public void ParseInternalPath_NoPortSpecified_DefaultsTo22()
    {
        var (user, host, port, remotePath) = SftpProvider.ParseInternalPath("alice@host/path/to/file");

        port.Should().Be(22);
        remotePath.Should().Be("/path/to/file");
    }

    // ---- 通过公共 API 间接验证路径解析 (IsValidPath / NormalizePath) ----

    [Theory]
    [InlineData("alice@host/path", true)]
    [InlineData("alice@host:22/path", true)]
    [InlineData("alice@host", true)]                  // 无 path 部分也算合法
    [InlineData("@host/path", false)]
    [InlineData("alice@", false)]
    [InlineData("", false)]
    public void IsValidPath_ReturnsCorrectResult(string internalPath, bool expected)
    {
        var provider = new SftpProvider(new NullCredProvider());
        try
        {
            var path = new ItemPath { Provider = "sftp", InternalPath = internalPath };

            provider.IsValidPath(path).Should().Be(expected);
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void IsValidPath_NonSftpProvider_ReturnsFalse()
    {
        var provider = new SftpProvider(new NullCredProvider());
        try
        {
            var path = new ItemPath { Provider = "fs", InternalPath = "alice@host/path" };

            provider.IsValidPath(path).Should().BeFalse();
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void NormalizePath_ReturnsCanonicalForm()
    {
        var provider = new SftpProvider(new NullCredProvider());
        try
        {
            // 不带 port → NormalizePath 补上默认 :22。
            var path = new ItemPath { Provider = "sftp", InternalPath = "alice@host/path/to/file" };

            var normalized = provider.NormalizePath(path);

            normalized.Provider.Should().Be("sftp");
            normalized.InternalPath.Should().Be("alice@host:22/path/to/file");
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void NormalizePath_WithCustomPort_PreservesPort()
    {
        var provider = new SftpProvider(new NullCredProvider());
        try
        {
            var path = new ItemPath { Provider = "sftp", InternalPath = "alice@host:2222/some/path" };

            var normalized = provider.NormalizePath(path);

            normalized.InternalPath.Should().Be("alice@host:2222/some/path");
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void NormalizePath_InvalidPath_ThrowsArgumentException()
    {
        var provider = new SftpProvider(new NullCredProvider());
        try
        {
            var path = new ItemPath { Provider = "sftp", InternalPath = "@host" };

            var act = () => provider.NormalizePath(path);

            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            provider.Dispose();
        }
    }

    /// <summary>测试用 ICredentialProvider 桩: 永远返回 null 凭据。</summary>
    private sealed class NullCredProvider : ICredentialProvider
    {
        public SftpCredentials? GetCredentials(string host, string user) => null;
    }
}
