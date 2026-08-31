#nullable enable

using FluentAssertions;
using OpenShell.Core.Tests.CliE2E;
using Xunit;

namespace OpenShell.Core.Tests;

/// <summary>
/// 最新项目可用性合规测试。
/// </summary>
public sealed class LatestProjectComplianceTests
{
    [Fact]
    public async Task Missing_Mandatory_Arguments_Return_InvalidArgument()
    {
        var result = await CliProcessRunner.RunCommandAsync("set-config");
        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("required");
    }

    [Fact]
    public async Task Unknown_Command_Parameter_Returns_InvalidArgument()
    {
        var result = await CliProcessRunner.RunCommandAsync("get-date -Bogus");
        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("Bogus");
    }

    [Fact]
    public async Task Destructive_Command_Missing_Path_Does_Not_Default_To_CurrentDirectory()
    {
        var result = await CliProcessRunner.RunCommandAsync("new-item");
        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("required");
    }

    [Fact]
    public async Task Property_Command_Output_Contains_Actual_Value()
    {
        var result = await CliProcessRunner.RunCommandAsync("get-date -format yyyy");
        result.Succeeded.Should().BeTrue();
        result.Stdout.Should().MatchRegex(@"\b20\d{2}\b");
    }

    [Fact]
    public async Task Ast_Command_Unknown_Parameter_Is_Not_Silent()
    {
        var result = await CliProcessRunner.RunCommandAsync("if ($true) { get-date -Bogus }");
        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("Bogus");
    }
}
