using System.IO;
using FluentAssertions;
using NSubstitute;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// ADR-0049 §3: <see cref="ShouldProcessService"/> unit tests.
/// Verifies WhatIf mode, impact-threshold comparison, session-level YesToAll / NoToAll,
/// <see cref="IShouldProcessService.ShouldContinue(string, string)"/> always-prompt semantics,
/// and <see cref="IShouldProcessService.ResetSessionConfirmState"/> behavior.
/// </summary>
public class ShouldProcessServiceTests
{
    /// <summary>
    /// Test stub prompter: returns pre-queued responses in order, counts calls,
    /// and tracks the last target/action it was invoked with.
    /// </summary>
    private sealed class StubPrompter : IConfirmationPrompter
    {
        private readonly Queue<(bool Result, bool YesToAll, bool NoToAll)> _responses = new();

        public int CallCount { get; private set; }
        public string? LastTarget { get; private set; }
        public string? LastAction { get; private set; }

        /// <summary>ADR-0049 §10: Suspend 回调。测试不演练 Suspend 路径, 保持 null (降级为 No)。</summary>
        public Action<string, string>? SuspendCallback { get; set; }

        public void Enqueue(bool result, bool yesToAll = false, bool noToAll = false)
            => _responses.Enqueue((result, yesToAll, noToAll));

        public bool PromptYesNoAll(string target, string action, out bool yesToAll, out bool noToAll)
        {
            CallCount++;
            LastTarget = target;
            LastAction = action;
            if (_responses.Count == 0)
            {
                yesToAll = false;
                noToAll = false;
                return false;
            }
            var (result, ya, na) = _responses.Dequeue();
            yesToAll = ya;
            noToAll = na;
            return result;
        }

        /// <summary>
        /// ADR-0049 §3.2: 完整选择提示。ShouldProcessService 当前仅走 PromptYesNoAll 路径,
        /// 此方法仅供接口契约完整; 复用同一队列并映射到 ConfirmationChoice。
        /// </summary>
        public ConfirmationChoice Prompt(string target, string action)
        {
            var yes = PromptYesNoAll(target, action, out var yesToAll, out var noToAll);
            if (yesToAll) return ConfirmationChoice.YesToAll;
            if (noToAll) return ConfirmationChoice.NoToAll;
            return yes ? ConfirmationChoice.Yes : ConfirmationChoice.No;
        }
    }

    [Fact]
    public void ShouldProcess_WhatIfPreferenceTrue_ReturnsFalseAndWritesWhatIfMessage()
    {
        // Arrange: redirect stderr so we can capture the "What if: ..." line.
        var prompter = new StubPrompter();
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = true,
            ConfirmPreference = ConfirmPreference.High,
        };
        var captured = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(captured);

        try
        {
            // Act
            var result = svc.ShouldProcess("C:/temp/foo.txt", "Remove", ConfirmImpact.High);

            // Assert
            result.Should().BeFalse("WhatIf mode must skip the action");
            prompter.CallCount.Should().Be(0, "WhatIf mode must not prompt");
            captured.ToString().Should().Contain("What if:");
            // ADR-0049 §3.1: WhatIf 输出用单引号包裹 action / target.
            captured.ToString().Should().Contain("'Remove'");
            captured.ToString().Should().Contain("'C:/temp/foo.txt'");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void ShouldProcess_ImpactBelowThreshold_ReturnsTrueWithoutPrompt()
    {
        // Arrange: ConfirmPreference = High, impact = Low → Low < High → no prompt.
        var prompter = new StubPrompter();
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.High,
        };

        var result = svc.ShouldProcess("target", "action", ConfirmImpact.Low);

        result.Should().BeTrue();
        prompter.CallCount.Should().Be(0);
    }

    [Fact]
    public void ShouldProcess_ImpactAtThreshold_UserTypesY_ReturnsTrue()
    {
        // Arrange: ConfirmPreference = Medium, impact = Medium → prompt → user types Y.
        var prompter = new StubPrompter();
        prompter.Enqueue(result: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.Medium,
        };

        var result = svc.ShouldProcess("target", "action", ConfirmImpact.Medium);

        result.Should().BeTrue();
        prompter.CallCount.Should().Be(1);
    }

    [Fact]
    public void ShouldProcess_UserTypesYesToAll_SubsequentCallsSkipPrompt()
    {
        var prompter = new StubPrompter();
        prompter.Enqueue(result: true, yesToAll: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.Medium,
        };

        // First call: prompts, user picks "Yes to All".
        var first = svc.ShouldProcess("t1", "a1", ConfirmImpact.Medium);
        first.Should().BeTrue();
        prompter.CallCount.Should().Be(1);

        // Second call: YesToAll already set → no prompt, returns true.
        var second = svc.ShouldProcess("t2", "a2", ConfirmImpact.Medium);
        second.Should().BeTrue();
        prompter.CallCount.Should().Be(1, "YesToAll should short-circuit subsequent calls");
    }

    [Fact]
    public void ShouldProcess_UserTypesNo_ReturnsFalse()
    {
        var prompter = new StubPrompter();
        prompter.Enqueue(result: false);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.Medium,
        };

        var result = svc.ShouldProcess("target", "action", ConfirmImpact.Medium);

        result.Should().BeFalse();
        prompter.CallCount.Should().Be(1);
    }

    [Fact]
    public void ShouldProcess_UserTypesNoToAll_SubsequentCallsReturnFalseWithoutPrompt()
    {
        var prompter = new StubPrompter();
        prompter.Enqueue(result: false, noToAll: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.Medium,
        };

        var first = svc.ShouldProcess("t1", "a1", ConfirmImpact.Medium);
        first.Should().BeFalse();
        prompter.CallCount.Should().Be(1);

        var second = svc.ShouldProcess("t2", "a2", ConfirmImpact.Medium);
        second.Should().BeFalse();
        prompter.CallCount.Should().Be(1, "NoToAll should short-circuit subsequent calls");
    }

    [Fact]
    public void ShouldProcess_ConfirmPreferenceNone_AlwaysReturnsTrue()
    {
        var prompter = new StubPrompter();
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.None,
        };

        // Even High impact should not prompt when ConfirmPreference is None.
        var result = svc.ShouldProcess("target", "action", ConfirmImpact.High);
        result.Should().BeTrue();
        prompter.CallCount.Should().Be(0);
    }

    [Fact]
    public void ShouldProcess_ImpactNone_AlwaysReturnsTrue()
    {
        var prompter = new StubPrompter();
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.High,
        };

        // Impact None means action is non-destructive; never prompt.
        var result = svc.ShouldProcess("target", "action", ConfirmImpact.None);
        result.Should().BeTrue();
        prompter.CallCount.Should().Be(0);
    }

    [Fact]
    public void ShouldContinue_AlwaysPromptsRegardlessOfImpact()
    {
        var prompter = new StubPrompter();
        prompter.Enqueue(result: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.None,  // Even None should still prompt for ShouldContinue.
        };

        var result = svc.ShouldContinue("target", "action");

        result.Should().BeTrue();
        prompter.CallCount.Should().Be(1, "ShouldContinue ignores impact / ConfirmPreference");
    }

    [Fact]
    public void ShouldContinue_WhatIfTrue_StillPrompts()
    {
        // Per ADR-0049 §4: ShouldContinue does NOT read WhatIfPreference.
        // The caller is responsible for guarding it with ShouldProcess first.
        var prompter = new StubPrompter();
        prompter.Enqueue(result: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = true,
            ConfirmPreference = ConfirmPreference.High,
        };

        var result = svc.ShouldContinue("target", "action");

        result.Should().BeTrue();
        prompter.CallCount.Should().Be(1);
    }

    [Fact]
    public void ResetSessionConfirmState_ClearsYesToAllAndNoToAll()
    {
        var prompter = new StubPrompter();
        prompter.Enqueue(result: true, yesToAll: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.Medium,
        };

        // Trigger YesToAll via first call.
        _ = svc.ShouldProcess("t1", "a1", ConfirmImpact.Medium);
        prompter.CallCount.Should().Be(1);

        // Reset state.
        svc.ResetSessionConfirmState();

        // Next call should prompt again because YesToAll was cleared.
        prompter.Enqueue(result: false);
        var result = svc.ShouldProcess("t2", "a2", ConfirmImpact.Medium);
        result.Should().BeFalse();
        prompter.CallCount.Should().Be(2, "Reset should allow subsequent calls to prompt");
    }

    [Fact]
    public void ResetSessionConfirmState_ClearsNoToAll()
    {
        var prompter = new StubPrompter();
        prompter.Enqueue(result: false, noToAll: true);
        var svc = new ShouldProcessService(prompter)
        {
            WhatIfPreference = false,
            ConfirmPreference = ConfirmPreference.Medium,
        };

        _ = svc.ShouldProcess("t1", "a1", ConfirmImpact.Medium);
        prompter.CallCount.Should().Be(1);

        svc.ResetSessionConfirmState();

        prompter.Enqueue(result: true);
        var result = svc.ShouldProcess("t2", "a2", ConfirmImpact.Medium);
        result.Should().BeTrue();
        prompter.CallCount.Should().Be(2);
    }

    [Fact]
    public void Defaults_HighConfirmPreference_PowerShellParity()
    {
        // Per ADR-0049 §2: default $ConfirmPreference = 'High'.
        var svc = new ShouldProcessService(Substitute.For<IConfirmationPrompter>());
        svc.ConfirmPreference.Should().Be(ConfirmPreference.High);
        svc.WhatIfPreference.Should().BeFalse();
    }
}
