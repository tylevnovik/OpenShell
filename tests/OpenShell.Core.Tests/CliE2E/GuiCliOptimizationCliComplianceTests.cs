#nullable enable

using FluentAssertions;
using OpenShell.Errors;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.CliE2E;

/// <summary>GUI/CLI 产品化主题中的 CLI 进程级合规测试。</summary>
public sealed class GuiCliOptimizationCliComplianceTests
{
    [Fact]
    public async Task NonInteractiveOutput_UsesCleanStreams()
    {
        var result = await CliProcessRunner.RunCommandAsync("pwd");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Stderr.Should().BeNullOrWhiteSpace();
        result.Stdout.Should().NotContain("info:");
        result.Stdout.Should().NotContain("Application started");
        result.Stdout.Should().NotContain("OpenShell Shell");
    }

    [Fact]
    public async Task InteractiveStartup_DoesNotEmitFrameworkLogs()
    {
        var result = await CliProcessRunner.RunAsync(
            Array.Empty<string>(),
            timeoutMs: 10000,
            standardInput: "exit" + Environment.NewLine);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Stdout.Should().Contain("OpenShell");
        result.Stdout.Should().NotContain("info:")
            .And.NotContain("Application started")
            .And.NotContain("Hosting environment");
    }

    [Fact]
    public async Task UnicodeOutput_RoundTripsAsUtf8()
    {
        var result = await CliProcessRunner.RunCommandAsync("Write-Output \"你好，OpenShell\"");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Stderr.Should().BeNullOrWhiteSpace();
        result.Stdout.Should().Contain("你好，OpenShell");
    }

    [Fact]
    public async Task HelpAndVersion_AreSideEffectFree()
    {
        var help = await CliProcessRunner.RunAsync(new[] { "--help" });
        var version = await CliProcessRunner.RunAsync(new[] { "--version" });

        help.ExitCode.Should().Be(ExitCodes.Success);
        help.Stderr.Should().BeNullOrWhiteSpace();
        help.Stdout.Should().Contain("OpenShell").And.Contain("--command");
        help.Stdout.Should().NotContain("Application started").And.NotContain("Providers:");

        version.ExitCode.Should().Be(ExitCodes.Success);
        version.Stderr.Should().BeNullOrWhiteSpace();
        version.Stdout.Trim().Should().MatchRegex("^OpenShell 0\\.1\\.0-alpha");
        version.Stdout.Should().NotContain("Application started");
    }

    [Theory]
    [InlineData("--command")]
    [InlineData("--file")]
    [InlineData("--profile")]
    [InlineData("--session")]
    [InlineData("--execution-policy")]
    [InlineData("--definitely-unknown")]
    public async Task InvalidInvocation_ReturnsUsageError(string argument)
    {
        var result = await CliProcessRunner.RunAsync(new[] { argument });

        result.ExitCode.Should().Be(ExitCodes.InvalidArgument);
        result.Stdout.Should().BeNullOrWhiteSpace();
        result.Stderr.Should().Contain(argument).And.Contain("--help");
    }

    [Fact]
    public async Task CommandAndFileModes_AreMutuallyExclusive()
    {
        var result = await CliProcessRunner.RunAsync(
            new[] { "--command", "pwd", "--file", "script.osh" });

        result.ExitCode.Should().Be(ExitCodes.InvalidArgument);
        result.Stdout.Should().BeNullOrWhiteSpace();
        result.Stderr.Should().Contain("--command").And.Contain("--file");
    }

    [Fact]
    public async Task InvalidExecutionPolicy_ReturnsUsageError()
    {
        var result = await CliProcessRunner.RunAsync(
            new[] { "--execution-policy", "DefinitelyInvalid", "--command", "pwd" });

        result.ExitCode.Should().Be(ExitCodes.InvalidArgument);
        result.Stdout.Should().BeNullOrWhiteSpace();
        result.Stderr.Should().Contain("DefinitelyInvalid");
    }

    [Fact]
    public async Task CommandFailure_UsesMappedExitCode()
    {
        var result = await CliProcessRunner.RunCommandAsync("definitely-not-a-command");

        result.ExitCode.Should().Be(ExitCodes.CommandNotFound);
        result.Stdout.Should().BeNullOrWhiteSpace();
        result.Stderr.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ScriptParseFailure_UsesParseExitCode()
    {
        using var tempDir = new TempDir();
        var scriptPath = Path.Combine(tempDir.FullPath, "invalid.osh");
        File.WriteAllText(scriptPath, "if (");

        var result = await CliProcessRunner.RunFileAsync(scriptPath, tempDir.FullPath);

        result.ExitCode.Should().Be(ExitCodes.ParseError);
        result.Stdout.Should().BeNullOrWhiteSpace();
        result.Stderr.Should().Contain("Parse error");
    }

    [Fact]
    public async Task ScriptFile_WriteOutput_EmitsWholeStrings()
    {
        // D-628: AST 路径把字符串绑定到 string[] 参数时不得按字符枚举。
        using var tempDir = new TempDir();
        var scriptPath = Path.Combine(tempDir.FullPath, "out.osh");
        File.WriteAllText(scriptPath, "Write-Output \"hello openshell\"\nWrite-Output bare-token");

        var result = await CliProcessRunner.RunFileAsync(scriptPath, tempDir.FullPath);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Stderr.Should().BeNullOrWhiteSpace();
        result.Stdout.Should().Contain("hello openshell", "引号字符串应作为整体单项输出");
        result.Stdout.Should().Contain("bare-token", "裸 token 应作为整体单项输出");
        // 每条语句各自产出 1 项（各带一行 "-- 1 项" 汇总），而非按字符展开成十几项。
        var summaryCount = result.Stdout.Split("\n").Count(l => l.Contains("-- 1 项"));
        summaryCount.Should().Be(2, "两条 Write-Output 各产出单项汇总，而非按字符展开");
    }

    [Fact]
    public async Task ParallelNonInteractiveInvocations_DoNotContendSessionLocks()
    {
        // D-627: 并行一次性调用共享 "default" 会话；修复后非交互模式不触碰会话锁，
        // 所有调用的 stderr 必须保持干净且输出互不污染。
        var tasks = Enumerable.Range(0, 6)
            .Select(i => CliProcessRunner.RunCommandAsync($"Write-Output ready-{i}"))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
        {
            results[i].ExitCode.Should().Be(ExitCodes.Success, $"第 {i} 个并行调用应成功");
            results[i].Stderr.Should().BeNullOrWhiteSpace($"第 {i} 个并行调用的 stderr 必须干净");
            results[i].Stdout.Should().Contain($"ready-{i}");
        }
    }

    [Fact]
    public async Task NonInteractiveInvocation_PreservesInFlightSessionLock()
    {
        // D-627: 已有存活进程持有 "default" 锁时，一次性执行不得覆盖或删除该锁，
        // 也不得把会话诊断写入 stderr。
        var lockPath = Path.Combine(OpenShell.OpenShellPaths.SessionsDir, "default.lock");
        Directory.CreateDirectory(OpenShell.OpenShellPaths.SessionsDir);
        var backup = File.Exists(lockPath) ? File.ReadAllText(lockPath) : null;
        var sentinel = $"{{\"pid\":{Environment.ProcessId},\"started\":\"2026-01-01T00:00:00Z\",\"machine\":\"test\"}}";
        try
        {
            File.WriteAllText(lockPath, sentinel);

            var result = await CliProcessRunner.RunCommandAsync("pwd");

            result.ExitCode.Should().Be(ExitCodes.Success);
            result.Stderr.Should().BeNullOrWhiteSpace();
            File.Exists(lockPath).Should().BeTrue("非交互调用不得删除在途会话锁");
            File.ReadAllText(lockPath).Should().Be(sentinel, "非交互调用不得覆盖在途会话锁");
        }
        finally
        {
            if (backup is not null) File.WriteAllText(lockPath, backup);
            else if (File.Exists(lockPath)) File.Delete(lockPath);
        }
    }
}
