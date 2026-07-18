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
}
