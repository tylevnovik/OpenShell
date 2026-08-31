using System.Text;
using FluentAssertions;
using OpenShell.Security;
using Xunit;

namespace OpenShell.Core.Tests.Security;

public sealed class SecurePasswordPrompterTests
{
    [Fact]
    public async Task InteractiveInput_IsNotWrittenToOutput()
    {
        var keys = new Queue<ConsoleKeyInfo>([
            new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false),
            new ConsoleKeyInfo('e', ConsoleKey.E, false, false, false),
            new ConsoleKeyInfo('c', ConsoleKey.C, false, false, false),
            new ConsoleKeyInfo('r', ConsoleKey.R, false, false, false),
            new ConsoleKeyInfo('e', ConsoleKey.E, false, false, false),
            new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        ]);
        using var output = new StringWriter(new StringBuilder());
        var prompter = new ConsoleSecurePasswordPrompter(
            readKey: () => keys.Dequeue(),
            isInputRedirected: () => false,
            error: output);

        var result = await prompter.PromptPasswordAsync("Password:");

        result.Should().Be("secret");
        output.ToString().Should().Contain("Password:");
        output.ToString().Should().NotContain("secret");
    }

    [Fact]
    public async Task RedirectedInput_UsesReadLineWithoutWarning()
    {
        var output = new StringWriter();
        var prompter = new ConsoleSecurePasswordPrompter(
            readLine: () => "pipe-secret",
            isInputRedirected: () => true,
            error: output);

        var result = await prompter.PromptPasswordAsync("Password:");

        result.Should().Be("pipe-secret");
        output.ToString().Should().BeEmpty();
    }
}

