using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Logging;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Providers.Remote;
using OpenShell.Providers.Remote.Commands;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Providers.Remote.Tests;

/// <summary>
/// SFTP 命令参数校验测试。Per ADR-0019 §3, ADR-0026, ADR-0033.
/// 用真实 IServiceProvider 注入 InMemoryCredentialProvider (避免依赖具体 host 实现),
/// 验证参数边界条件 (password/privatekey 互斥, port 范围, host 必填) 与 ErrorRecord 写入。
/// 不测试真实 SFTP 连接 (TestSftpConnectionCommand.TestConnectionAsync 部分) — 那部分需 SSH test container。
/// </summary>
public class SftpCommandArgumentTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    private string CredFilePath => System.IO.Path.Combine(_tempDir.FullPath, "sftp-creds.json");

    /// <summary>构造一个 CommandContext, 其 Host.Services 返回真实的 ServiceProvider, 内含 InMemoryCredentialProvider。</summary>
    private (CommandContext ctx, InMemoryCredentialProvider credProvider, CapturingHost host) CreateContext()
    {
        var (ctx, credProvider, host, _) = CreateContext(registerSftpProvider: false);
        return (ctx, credProvider, host);
    }

    /// <summary>构造一个 CommandContext。若 registerSftpProvider 为 true, 同时注册一个 SftpProvider (用 NullCredentialProvider)。</summary>
    private (CommandContext ctx, InMemoryCredentialProvider credProvider, CapturingHost host, SftpProvider sftpProvider) CreateContext(bool registerSftpProvider)
    {
        var services = new ServiceCollection();
        var credProvider = new InMemoryCredentialProvider(CredFilePath);
        services.AddSingleton(credProvider);
        var sp = services.BuildServiceProvider();

        var providers = new ProviderRegistry();
        SftpProvider? sftpProvider = null;
        if (registerSftpProvider)
        {
            // 注入一个 SftpProvider, 但用永远返回 null 的 ICredentialProvider。
            // TestSftpConnectionCommand 只测端口校验, 不会真的发起连接 (端口校验在连接前)。
            sftpProvider = new SftpProvider(new NullCredentialProvider());
            providers.Register(sftpProvider);
        }

        var commands = new CommandRegistry();
        var errors = new InMemoryErrorStream(new InMemoryLogStore());
        var host = new CapturingHost(sp);

        var ctx = new CommandContext
        {
            Providers = providers,
            Commands = commands,
            Host = host,
            CurrentLocation = new ItemPath { Provider = "fs", InternalPath = _tempDir.FullPath.Replace('\\', '/') },
            Errors = errors,
            Operations = new OperationEngine(providers),
        };
        return (ctx, credProvider, host, sftpProvider!);
    }

    /// <summary>永远返回 null 凭据的 ICredentialProvider 桩, 用于构造 SftpProvider 而不依赖真实凭据。</summary>
    private sealed class NullCredentialProvider : ICredentialProvider
    {
        public SftpCredentials? GetCredentials(string host, string user) => null;
    }

    /// <summary>消费 IAsyncEnumerable 以触发命令执行。</summary>
    private static async Task<List<IItem>> DrainAsync(IAsyncEnumerable<IItem> items)
    {
        var list = new List<IItem>();
        await foreach (var item in items)
            list.Add(item);
        return list;
    }

    // ---- SetSftpCredentialCommand 参数校验 ----

    [Fact]
    public async Task SetSftpCredential_NeitherPasswordNorPrivateKey_WritesInvalidArgumentError()
    {
        var (ctx, credProvider, _) = CreateContext();
        var cmd = new SetSftpCredentialCommand();
        var args = new SetSftpCredentialCommand.Args("example.com", "alice");

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        var errs = ctx.Errors!.RecentErrors;
        errs.Should().HaveCount(1);
        errs[0].Category.Should().Be(ErrorCategory.InvalidArgument);
        errs[0].Operation.Should().Be("set-sftpcredential");
        errs[0].Phase.Should().Be(ErrorPhase.ArgumentBinding);
        errs[0].Message.Should().Contain("Password").And.Contain("PrivateKeyPath");

        // 没写入凭据。
        credProvider.ListCredentials().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(99999)]
    public async Task SetSftpCredential_InvalidPort_WritesInvalidArgumentError(int port)
    {
        var (ctx, credProvider, _) = CreateContext();
        var cmd = new SetSftpCredentialCommand();
        var args = new SetSftpCredentialCommand.Args("example.com", "alice", Password: "pw", Port: port);

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        var errs = ctx.Errors!.RecentErrors;
        errs.Should().HaveCount(1);
        errs[0].Category.Should().Be(ErrorCategory.InvalidArgument);
        errs[0].Message.Should().Contain("port").And.Contain(port.ToString());
        credProvider.ListCredentials().Should().BeEmpty();
    }

    [Fact]
    public async Task SetSftpCredential_ValidPassword_SavesAndWritesOutput()
    {
        var (ctx, credProvider, host) = CreateContext();
        var cmd = new SetSftpCredentialCommand();
        var args = new SetSftpCredentialCommand.Args("example.com", "alice", Password: "s3cret", Port: 22);

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        ctx.Errors!.RecentErrors.Should().BeEmpty();
        var all = credProvider.ListCredentials();
        all.Should().HaveCount(1);
        all[0].Host.Should().Be("example.com");
        all[0].User.Should().Be("alice");
        all[0].Port.Should().Be(22);
        // 输出应包含 host/user/port 信息。
        host.CapturedOutput.Should().ContainMatch("*Saved*alice@example.com:22*");
    }

    [Fact]
    public async Task SetSftpCredential_ValidPrivateKey_SavesAndWritesOutput()
    {
        var (ctx, credProvider, host) = CreateContext();
        var cmd = new SetSftpCredentialCommand();
        var args = new SetSftpCredentialCommand.Args(
            Host: "example.com",
            User: "alice",
            Password: null,
            Port: 22,
            PrivateKeyPath: "/home/alice/.ssh/id_rsa",
            PrivateKeyPassphrase: null);

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        ctx.Errors!.RecentErrors.Should().BeEmpty();
        var all = credProvider.ListCredentials();
        all.Should().HaveCount(1);
        all[0].PrivateKeyPath.Should().Be("/home/alice/.ssh/id_rsa");
        host.CapturedOutput.Should().ContainMatch("*private key*");
    }

    [Fact]
    public async Task SetSftpCredential_CredentialProviderNotRegistered_WritesNotImplementedError()
    {
        // 构造一个不带 InMemoryCredentialProvider 的 ServiceProvider。
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var errors = new InMemoryErrorStream(new InMemoryLogStore());
        var providers = new ProviderRegistry();
        var host = new CapturingHost(sp);
        var ctx = new CommandContext
        {
            Providers = providers,
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = default,
            Errors = errors,
            Operations = new OperationEngine(providers),
        };

        var cmd = new SetSftpCredentialCommand();
        var args = new SetSftpCredentialCommand.Args("example.com", "alice", Password: "pw");

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        var errs = errors.RecentErrors;
        errs.Should().HaveCount(1);
        errs[0].Category.Should().Be(ErrorCategory.NotImplemented);
        errs[0].Operation.Should().Be("set-sftpcredential");
    }

    // ---- GetSftpCredentialCommand ----

    [Fact]
    public async Task GetSftpCredential_NoCredsConfigured_WritesOutput()
    {
        var (ctx, _, host) = CreateContext();
        var cmd = new GetSftpCredentialCommand();
        var args = new GetSftpCredentialCommand.Args();

        var items = await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        items.Should().BeEmpty();
        host.CapturedOutput.Should().ContainMatch("*No SFTP credentials*");
    }

    [Fact]
    public async Task GetSftpCredential_WithCreds_ReturnsMaskedItems()
    {
        var (ctx, credProvider, _) = CreateContext();
        credProvider.SetCredentials(new SftpCredentials
        {
            Host = "example.com",
            User = "alice",
            Password = "s3cret",
            Port = 2222,
        });

        var cmd = new GetSftpCredentialCommand();
        var args = new GetSftpCredentialCommand.Args();

        var items = await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        items.Should().HaveCount(1);
        items[0].Kind.Should().Be(ItemKind.Property);
        items[0].Properties.Values.TryGetValue("Host", out var host).Should().BeTrue();
        host!.ToString().Should().Be("example.com");
        items[0].Properties.Values.TryGetValue("HasPassword", out var hasPw).Should().BeTrue();
        hasPw!.ToString().Should().Be("True");
    }

    [Fact]
    public async Task GetSftpCredential_WithHostFilter_ReturnsMatchingOnly()
    {
        var (ctx, credProvider, _) = CreateContext();
        credProvider.SetCredentials(new SftpCredentials { Host = "alpha.com", User = "alice", Password = "1" });
        credProvider.SetCredentials(new SftpCredentials { Host = "beta.com", User = "alice", Password = "2" });

        var cmd = new GetSftpCredentialCommand();
        var args = new GetSftpCredentialCommand.Args(Host: "alpha.com");

        var items = await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        items.Should().HaveCount(1);
        items[0].Properties.Values.TryGetValue("Host", out var host).Should().BeTrue();
        host!.ToString().Should().Be("alpha.com");
    }

    // ---- RemoveSftpCredentialCommand ----

    [Fact]
    public async Task RemoveSftpCredential_ExistingHost_ReturnsTrueAndWritesOutput()
    {
        var (ctx, credProvider, host) = CreateContext();
        credProvider.SetCredentials(new SftpCredentials { Host = "example.com", User = "alice", Password = "1" });
        credProvider.SetCredentials(new SftpCredentials { Host = "example.com", User = "bob", Password = "2" });

        var cmd = new RemoveSftpCredentialCommand();
        var args = new RemoveSftpCredentialCommand.Args(Host: "example.com");

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        ctx.Errors!.RecentErrors.Should().BeEmpty();
        host.CapturedOutput.Should().ContainMatch("*Removed*example.com*");
        credProvider.ListCredentials().Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveSftpCredential_ExistingHostAndUser_RemovesOnlyMatching()
    {
        var (ctx, credProvider, _) = CreateContext();
        credProvider.SetCredentials(new SftpCredentials { Host = "example.com", User = "alice", Password = "1" });
        credProvider.SetCredentials(new SftpCredentials { Host = "example.com", User = "bob", Password = "2" });

        var cmd = new RemoveSftpCredentialCommand();
        var args = new RemoveSftpCredentialCommand.Args(Host: "example.com", User: "alice");

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        ctx.Errors!.RecentErrors.Should().BeEmpty();
        credProvider.ListCredentials().Should().HaveCount(1);
        credProvider.GetCredentials("example.com", "alice").Should().BeNull();
        credProvider.GetCredentials("example.com", "bob").Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveSftpCredential_NonexistentHost_WritesItemNotFoundError()
    {
        var (ctx, _, _) = CreateContext();
        var cmd = new RemoveSftpCredentialCommand();
        var args = new RemoveSftpCredentialCommand.Args(Host: "nonexistent.com");

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        var errs = ctx.Errors!.RecentErrors;
        errs.Should().HaveCount(1);
        errs[0].Category.Should().Be(ErrorCategory.ItemNotFound);
        errs[0].Operation.Should().Be("remove-sftpcredential");
        errs[0].Suggestion.Should().NotBeNull();
    }

    // ---- TestSftpConnectionCommand 参数校验 ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task TestSftpConnection_InvalidPort_WritesInvalidArgumentError(int port)
    {
        // 注册 SftpProvider (用 NullCredentialProvider), 让命令跳过 ProviderNotFound 检查, 进入端口校验。
        var (ctx, _, _, sftpProvider) = CreateContext(registerSftpProvider: true);
        try
        {
            var cmd = new TestSftpConnectionCommand();
            var args = new TestSftpConnectionCommand.Args("example.com", "alice", Port: port);

            await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

            var errs = ctx.Errors!.RecentErrors;
            errs.Should().HaveCount(1);
            errs[0].Category.Should().Be(ErrorCategory.InvalidArgument);
            errs[0].Message.Should().Contain("port");
        }
        finally
        {
            sftpProvider.Dispose();
        }
    }

    [Fact]
    public async Task TestSftpConnection_ProviderNotRegistered_WritesProviderNotFoundError()
    {
        var (ctx, _, _) = CreateContext();
        // ctx.Providers 是空的 ProviderRegistry, 没注册 sftp provider。
        var cmd = new TestSftpConnectionCommand();
        var args = new TestSftpConnectionCommand.Args("example.com", "alice", Port: 22);

        await DrainAsync(cmd.ExecuteAsync(args, ctx, default));

        var errs = ctx.Errors!.RecentErrors;
        errs.Should().HaveCount(1);
        errs[0].Category.Should().Be(ErrorCategory.ProviderNotFound);
        errs[0].Operation.Should().Be("test-sftpconnection");
        errs[0].Phase.Should().Be(ErrorPhase.ProviderResolution);
    }

    /// <summary>
    /// 测试用 IHost 实现: 暴露真实 IServiceProvider + 捕获 WriteOutputLineAsync 输出。
    /// Selection / Progress 用空实现 (SFTP 命令不需要)。
    /// </summary>
    internal sealed class CapturingHost : IHost
    {
        private readonly List<string> _output = new();
        private readonly IServiceProvider _services;

        public CapturingHost(IServiceProvider services)
        {
            _services = services;
        }

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; }
        public IObservable<IReadOnlyList<IItem>> Selection { get; } = new EmptyObservable<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress { get; } = new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => _services;

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _output.Add(line);
            return Task.CompletedTask;
        }

        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IReadOnlyList<string> CapturedOutput => _output;
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnCompleted();
            return new EmptyDisposable();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
