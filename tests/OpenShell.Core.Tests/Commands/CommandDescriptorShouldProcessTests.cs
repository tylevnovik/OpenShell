using System.Runtime.CompilerServices;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Items;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// ADR-0049 §1 / §5 / §6: <see cref="CommandDescriptor.FromType"/> reads the
/// <c>[SupportsShouldProcess]</c> attribute and surfaces it as
/// <see cref="CommandDescriptor.SupportsShouldProcess"/> + <see cref="CommandDescriptor.ConfirmImpact"/>.
/// </summary>
public class CommandDescriptorShouldProcessTests
{
    /// <summary>Test fixture: a command with <c>[SupportsShouldProcess(ConfirmImpact = High)]</c>.</summary>
    [Verb("Test", Noun = "HighImpact")]
    [SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]
    [Description("Test command with High impact.")]
    public sealed class HighImpactTestCommand : ICommand<HighImpactTestCommand.Args>
    {
        public record Args;
        public async IAsyncEnumerable<IItem> ExecuteAsync(
            Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    /// <summary>Test fixture: a command with <c>[SupportsShouldProcess]</c> using the default impact (Medium).</summary>
    [Verb("Test", Noun = "DefaultImpact")]
    [SupportsShouldProcess]
    [Description("Test command with default impact.")]
    public sealed class DefaultImpactTestCommand : ICommand<DefaultImpactTestCommand.Args>
    {
        public record Args;
        public async IAsyncEnumerable<IItem> ExecuteAsync(
            Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    /// <summary>Test fixture: a non-destructive command without the attribute.</summary>
    [Verb("Test", Noun = "Noop")]
    [Description("Test command without SupportsShouldProcess.")]
    public sealed class NoopTestCommand : ICommand<NoopTestCommand.Args>
    {
        public record Args;
        public async IAsyncEnumerable<IItem> ExecuteAsync(
            Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public void FromType_ReadsSupportsShouldProcessAttribute()
    {
        var desc = CommandDescriptor.FromType(typeof(HighImpactTestCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
    }

    [Fact]
    public void FromType_ReadsConfirmImpactFromAttribute()
    {
        var desc = CommandDescriptor.FromType(typeof(HighImpactTestCommand));

        desc.ConfirmImpact.Should().Be(ConfirmImpact.High);
    }

    [Fact]
    public void FromType_SupportsShouldProcessWithoutExplicitImpact_DefaultsToMedium()
    {
        var desc = CommandDescriptor.FromType(typeof(DefaultImpactTestCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Medium);
    }

    [Fact]
    public void FromType_CommandWithoutAttribute_SupportsShouldProcessIsFalse()
    {
        var desc = CommandDescriptor.FromType(typeof(NoopTestCommand));

        desc.SupportsShouldProcess.Should().BeFalse();
        // ConfirmImpact default for non-attribute commands is Medium (per CommandDescriptor init).
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Medium);
    }

    [Fact]
    public void FromType_RealRemoveItemCommand_HasAttributeAndHighImpact()
    {
        // Per ADR-0049 §7 / task spec: RemoveItemCommand declares [SupportsShouldProcess(ConfirmImpact = High)].
        var desc = CommandDescriptor.FromType(typeof(RemoveItemCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.High);
    }

    [Fact]
    public void FromType_RealMoveItemCommand_HasAttributeAndLowImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(MoveItemCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Low);
    }

    [Fact]
    public void FromType_RealSetContentCommand_HasAttributeAndLowImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(SetContentCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Low);
    }

    [Fact]
    public void FromType_RealClearHistoryCommand_HasAttributeAndMediumImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(ClearHistoryCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Medium);
    }

    [Fact]
    public void FromType_RealRemovePSDriveCommand_HasAttributeAndMediumImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(RemovePSDriveCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Medium);
    }

    [Fact]
    public void FromType_RealRollbackUpdateCommand_HasAttributeAndHighImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(RollbackUpdateCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.High);
    }

    [Fact]
    public void FromType_RealUninstallProviderCommand_HasAttributeAndHighImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(UninstallProviderCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.High);
    }

    [Fact]
    public void FromType_RealRemoveVariableCommand_HasAttributeAndLowImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(RemoveVariableCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Low);
    }

    [Fact]
    public void FromType_RealCopyItemCommand_HasAttributeAndLowImpact()
    {
        var desc = CommandDescriptor.FromType(typeof(CopyItemCommand));

        desc.SupportsShouldProcess.Should().BeTrue();
        desc.ConfirmImpact.Should().Be(ConfirmImpact.Low);
    }

    [Fact]
    public void FromType_RealGetChildItemCommand_DoesNotHaveAttribute()
    {
        // Get-ChildItem is read-only — no [SupportsShouldProcess].
        var desc = CommandDescriptor.FromType(typeof(GetChildItemCommand));

        desc.SupportsShouldProcess.Should().BeFalse();
    }
}
