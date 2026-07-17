using OpenShell.Errors;
using OpenShell.Events;
using OpenShell.Filter;
using OpenShell.Paths;
using OpenShell.Providers.FileSystem;
using OpenShell.Providers.Remote;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.IntegrationTests;

/// <summary>
/// 项目稳定性合规测试。已实现特性必须通过，待修复特性用 pending T-XXX 标记。
/// </summary>
public sealed class ProjectStabilityComplianceTests
{
    [Fact]
    public void EventBus_Dispose_IsIdempotent()
    {
        var bus = new InProcessEventBus();

        bus.Dispose();
        bus.Dispose();

        bus.Publish(new TestEvent());
    }

    [Fact(Skip = "pending T-501")]
    public void ErrorRecord_MapsArgumentException()
    {
        var record = ErrorRecord.FromException(new ArgumentOutOfRangeException("count"));

        Assert.Equal(ErrorCategory.InvalidArgument, record.Category);
    }

    [Fact(Skip = "pending T-502")]
    public void FilterLexer_ParsesIsoDateLiteral()
    {
        var token = new Lexer("2026-07-18T12:34:56+08:00").Next();

        Assert.Equal(TokenKind.Date, token.Kind);
        Assert.IsType<DateTimeOffset>(token.Value);
    }

    [Fact(Skip = "pending T-504")]
    public async Task FileSystemProvider_HonorsPreCancelledToken()
    {
        using var tempDir = new TempDir();
        var provider = new FileSystemProvider();
        var path = new ItemPath("fs", tempDir.FullPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await provider.GetItemAsync(path, cts.Token));
    }

    [Fact(Skip = "pending T-504")]
    public async Task SftpProvider_HonorsPreCancelledTokenBeforeCredentialLookup()
    {
        using var provider = new SftpProvider(new NullCredentialProvider());
        var path = new ItemPath("sftp", "alice@example.com:22/home/alice");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await provider.GetItemAsync(path, cts.Token));
    }

    [Fact(Skip = "pending T-505")]
    public void Ci_UsesSlnxCompatibleSdk()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "ci.yml"));

        Assert.Contains("dotnet-version: '10.0.x'", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenShell.slnx")))
                return Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the OpenShell repository root.");
    }

    private sealed record TestEvent : IEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class NullCredentialProvider : ICredentialProvider
    {
        public SftpCredentials? GetCredentials(string host, string user) => null;
    }
}
