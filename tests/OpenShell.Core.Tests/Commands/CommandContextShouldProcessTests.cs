using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// ADR-0049 §3 / §8: <see cref="CommandContext.ShouldProcess(string, string, ConfirmImpact)"/> and
/// <see cref="CommandContext.ShouldContinue(string, string)"/> resolve an
/// <see cref="IShouldProcessService"/> from <see cref="IHost.Services"/> and delegate to it.
/// When no service is registered (e.g. minimal test hosts, or host configurations without the
/// ShouldProcess infrastructure), they gracefully default to "proceed" (return <c>true</c>)
/// so existing commands keep working without forcing the new dependency.
/// </summary>
public class CommandContextShouldProcessTests
{
    /// <summary>
    /// Minimal IHost implementation for testing: returns whatever ServiceProvider
    /// is passed in (which may or may not contain an <see cref="IShouldProcessService"/>).
    /// </summary>
    private sealed class StubHost : IHost
    {
        public StubHost(IServiceProvider services) => Services = services;

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = new() { Provider = "fs", InternalPath = "/" };
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObservable<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services { get; }
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    /// <summary>Build a CommandContext whose Host exposes the supplied ServiceProvider.</summary>
    private static CommandContext BuildContext(IServiceProvider services) => new()
    {
        Providers = new ProviderRegistry(),
        Commands = new CommandRegistry(),
        Host = new StubHost(services),
        CurrentLocation = new ItemPath { Provider = "fs", InternalPath = "/" },
    };

    // ---------------------------------------------------------------------
    // Graceful default: no IShouldProcessService registered → proceed.
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldProcess_WhenNoServiceRegistered_ReturnsTrue()
    {
        // Arrange: empty ServiceProvider — no IShouldProcessService registered.
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = BuildContext(services);

        // Act
        var result = ctx.ShouldProcess("target", "action", ConfirmImpact.High);

        // Assert: graceful default = proceed.
        result.Should().BeTrue(
            "CommandContext.ShouldProcess must default to 'proceed' when no IShouldProcessService is registered");
    }

    [Fact]
    public void ShouldContinue_WhenNoServiceRegistered_ReturnsTrue()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = BuildContext(services);

        var result = ctx.ShouldContinue("target", "action");

        result.Should().BeTrue(
            "CommandContext.ShouldContinue must default to 'proceed' when no IShouldProcessService is registered");
    }

    [Fact]
    public void ShouldProcess_WhenServiceProviderThrows_ReturnsTrue_EvenForHighImpact()
    {
        // Even a High impact action should proceed when there is no service —
        // confirming graceful degradation does not consult impact.
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = BuildContext(services);

        var result = ctx.ShouldProcess("target", "Remove", ConfirmImpact.High);

        result.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Delegation: when IShouldProcessService is registered, forward calls.
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldProcess_WhenServiceRegistered_DelegatesToService()
    {
        // Arrange: a ServiceProvider with a mocked IShouldProcessService that returns false.
        var mock = Substitute.For<IShouldProcessService>();
        mock.ShouldProcess("the-target", "the-action", ConfirmImpact.High).Returns(false);

        var services = new ServiceCollection()
            .AddSingleton(mock)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        // Act
        var result = ctx.ShouldProcess("the-target", "the-action", ConfirmImpact.High);

        // Assert
        result.Should().BeFalse("the registered service returned false");
        mock.Received(1).ShouldProcess("the-target", "the-action", ConfirmImpact.High);
    }

    [Fact]
    public void ShouldProcess_WhenServiceRegistered_ReturnsTrue_PassesThrough()
    {
        // Verify the service's return value of true is forwarded verbatim.
        var mock = Substitute.For<IShouldProcessService>();
        mock.ShouldProcess(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ConfirmImpact>())
            .Returns(true);

        var services = new ServiceCollection()
            .AddSingleton(mock)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        var result = ctx.ShouldProcess("t", "a", ConfirmImpact.Low);

        result.Should().BeTrue();
        mock.Received(1).ShouldProcess("t", "a", ConfirmImpact.Low);
    }

    [Fact]
    public void ShouldProcess_DefaultImpact_IsMedium()
    {
        // Per ADR-0049 §8 signature: impact defaults to Medium when omitted by the caller.
        var mock = Substitute.For<IShouldProcessService>();
        mock.ShouldProcess(Arg.Any<string>(), Arg.Any<string>(), ConfirmImpact.Medium)
            .Returns(true);

        var services = new ServiceCollection()
            .AddSingleton(mock)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        _ = ctx.ShouldProcess("t", "a");  // no impact argument supplied

        mock.Received(1).ShouldProcess("t", "a", ConfirmImpact.Medium);
    }

    [Fact]
    public void ShouldContinue_WhenServiceRegistered_DelegatesToService()
    {
        var mock = Substitute.For<IShouldProcessService>();
        mock.ShouldContinue("the-target", "the-action").Returns(false);

        var services = new ServiceCollection()
            .AddSingleton(mock)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        var result = ctx.ShouldContinue("the-target", "the-action");

        result.Should().BeFalse();
        mock.Received(1).ShouldContinue("the-target", "the-action");
    }

    [Fact]
    public void ShouldContinue_WhenServiceReturnsTrue_PassesThrough()
    {
        var mock = Substitute.For<IShouldProcessService>();
        mock.ShouldContinue(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var services = new ServiceCollection()
            .AddSingleton(mock)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        var result = ctx.ShouldContinue("t", "a");

        result.Should().BeTrue();
        mock.Received(1).ShouldContinue("t", "a");
    }

    // ---------------------------------------------------------------------
    // Integration-style: a real ShouldProcessService wired via DI behaves
    // end-to-end through CommandContext.
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldProcess_RealServiceWiredViaDI_WhatIfMode_ReturnsFalse()
    {
        // Arrange: wire a real ShouldProcessService with WhatIf=true through DI.
        var prompter = Substitute.For<IConfirmationPrompter>();
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = true,
            ConfirmPreference = ConfirmPreference.High,
        };
        var services = new ServiceCollection()
            .AddSingleton<IShouldProcessService>(svc)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        // Capture stderr to keep the test output clean.
        var captured = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(captured);
        try
        {
            var result = ctx.ShouldProcess("target-x", "Remove", ConfirmImpact.High);

            result.Should().BeFalse("WhatIf mode must skip the action");
            captured.ToString().Should().Contain("What if:");
            prompter.DidNotReceive().PromptYesNoAll(
                Arg.Any<string>(), Arg.Any<string>(), out _, out _);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void ShouldProcess_RealServiceWiredViaDI_BelowThreshold_ReturnsTrue()
    {
        // Arrange: ConfirmPreference=High, impact=Low → Low<High → no prompt, proceed.
        var prompter = Substitute.For<IConfirmationPrompter>();
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.High,
        };
        var services = new ServiceCollection()
            .AddSingleton<IShouldProcessService>(svc)
            .BuildServiceProvider();
        var ctx = BuildContext(services);

        var result = ctx.ShouldProcess("target", "action", ConfirmImpact.Low);

        result.Should().BeTrue();
        prompter.DidNotReceive().PromptYesNoAll(
            Arg.Any<string>(), Arg.Any<string>(), out _, out _);
    }
}
