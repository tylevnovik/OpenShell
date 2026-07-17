namespace OpenShell.Commands;

/// <summary>
/// Default <see cref="IShouldProcessService"/>. Per ADR-0049 §3.
/// Holds the WhatIf / ConfirmPreference thresholds and the per-call session-level
/// YesToAll / NoToAll state. The host is responsible for setting the preference
/// flags (typically from <c>-WhatIf</c> / <c>-Confirm</c> CLI args) and calling
/// <see cref="ResetSessionConfirmState"/> between command invocations.
/// </summary>
public sealed class ShouldProcessService : IShouldProcessService
{
    private readonly IConfirmationPrompter _prompter;
    private bool _yesToAll;
    private bool _noToAll;

    /// <summary>Construct a service with the given prompter.</summary>
    public ShouldProcessService(IConfirmationPrompter prompter)
    {
        _prompter = prompter ?? throw new ArgumentNullException(nameof(prompter));
    }

    /// <inheritdoc />
    public bool WhatIfPreference { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Defaults to <see cref="ConfirmPreference.High"/> (PowerShell parity: only High
    /// impact actions auto-prompt unless the user passes <c>-Confirm</c> or lowers the threshold).
    /// </remarks>
    public ConfirmPreference ConfirmPreference { get; set; } = ConfirmPreference.High;

    /// <inheritdoc />
    public bool ShouldProcess(string target, string action, ConfirmImpact impact)
    {
        // 1. WhatIf dry-run: print "What if: ..." and skip the action. Per ADR-0049 §3.1.
        if (WhatIfPreference)
        {
            // Per ADR-0049 §3.1: 使用单引号包裹 action 与 target.
            Console.Error.WriteLine(
                $"What if: Performing the operation '{action}' on target '{target}'.");
            return false;
        }

        // 2. User explicitly disabled all confirms OR action is non-destructive → proceed.
        if (ConfirmPreference == ConfirmPreference.None) return true;
        if (impact == ConfirmImpact.None) return true;

        // 3. impact below the threshold → no prompt.
        if ((int)impact < (int)ConfirmPreference) return true;

        // 4. Session-level YesToAll / NoToAll already chosen earlier in this call.
        if (_yesToAll) return true;
        if (_noToAll) return false;

        // 5. Interactive prompt.
        return _prompter.PromptYesNoAll(target, action, out _yesToAll, out _noToAll);
    }

    /// <inheritdoc />
    public bool ShouldContinue(string target, string action)
    {
        // Always prompt regardless of impact / WhatIf / ConfirmPreference.
        // Per ADR-0049 §4: callers decide when (and whether) to invoke.
        if (_yesToAll) return true;
        if (_noToAll) return false;
        return _prompter.PromptYesNoAll(target, action, out _yesToAll, out _noToAll);
    }

    /// <inheritdoc />
    public void ResetSessionConfirmState()
    {
        _yesToAll = false;
        _noToAll = false;
    }
}
