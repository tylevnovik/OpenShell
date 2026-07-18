#nullable enable

using FluentAssertions;
using OpenShell.Errors;
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

    [Fact(Skip = "pending T-622")]
    public async Task CommandFailure_UsesMappedExitCode()
    {
        var result = await CliProcessRunner.RunCommandAsync("definitely-not-a-command");

        result.ExitCode.Should().Be(ExitCodes.CommandNotFound);
        result.Stdout.Should().BeNullOrWhiteSpace();
        result.Stderr.Should().NotBeNullOrWhiteSpace();
    }
}
