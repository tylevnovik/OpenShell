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

    [Fact]
    public void ErrorRecord_MapsArgumentException()
    {
        var record = ErrorRecord.FromException(new ArgumentOutOfRangeException("count"));

        Assert.Equal(ErrorCategory.InvalidArgument, record.Category);
    }

    [Fact]
    public void FilterLexer_ParsesIsoDateLiteral()
    {
        var token = new Lexer("2026-07-18T12:34:56+08:00").Next();

        Assert.Equal(TokenKind.Date, token.Kind);
        Assert.IsType<DateTimeOffset>(token.Value);
    }

    [Fact]
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

    [Fact]
    public async Task SftpProvider_HonorsPreCancelledTokenBeforeCredentialLookup()
    {
        using var provider = new SftpProvider(new NullCredentialProvider());
        var path = new ItemPath("sftp", "alice@example.com:22/home/alice");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await provider.GetItemAsync(path, cts.Token));
    }

    [Fact]
    public void Ci_UsesSlnxCompatibleSdk()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "ci.yml"));

        Assert.Contains("dotnet-version: '10.0.x'", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_UsesCiAlignedSdkAndBashExpansion()
    {
        // D-508: 发布流水线必须与 ci.yml 的 SDK 对齐，且 ${GITHUB_REF_NAME#v} 只在 bash 下展开。
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        Assert.DoesNotContain("8.0.x", workflow, StringComparison.Ordinal);
        var sdkCount = workflow.Split("dotnet-version: '10.0.x'").Length - 1;
        Assert.Equal(2, sdkCount);

        // 逐步骤块检查：使用版本号展开的 run 步骤必须声明 shell: bash
        // （Windows runner 默认 pwsh，不支持 ${VAR#prefix} 参数展开）。
        foreach (var step in workflow.Split("- name:"))
        {
            if (step.Contains("${GITHUB_REF_NAME#v}", StringComparison.Ordinal)
                && step.Contains("run:", StringComparison.Ordinal))
            {
                Assert.True(step.Contains("shell: bash", StringComparison.Ordinal),
                    "使用 ${GITHUB_REF_NAME#v} 展开的步骤必须声明 shell: bash: "
                    + step.Split('\n')[0]);
            }
        }
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
