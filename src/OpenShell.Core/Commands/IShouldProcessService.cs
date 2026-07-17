namespace OpenShell.Commands;

/// <summary>
/// Per-ADR-0049 §3: <c>ShouldProcess</c> service. Encapsulates the WhatIf / ConfirmPreference
/// thresholds and the in-call session-level YesToAll / NoToAll state. Commands obtain it via
/// <see cref="CommandContext.ShouldProcess(string, string, ConfirmImpact)"/> (which gracefully
/// degrades to "always proceed" when no service is registered).
/// </summary>
public interface IShouldProcessService
{
    /// <summary>
    /// Decide whether a destructive action should proceed.
    /// Returns <c>false</c> when in WhatIf mode (after writing a "What if: ..." line to stderr)
    /// or when the user rejects the prompt; returns <c>true</c> otherwise.
    /// </summary>
    /// <param name="target">Human-readable description of the operation target.</param>
    /// <param name="action">Human-readable description of the operation action.</param>
    /// <param name="impact">Static destructive impact of the action.</param>
    /// <returns><c>true</c> to proceed; <c>false</c> to skip the action.</returns>
    bool ShouldProcess(string target, string action, ConfirmImpact impact);

    /// <summary>
    /// Force-prompt the user regardless of impact / WhatIf. Per ADR-0049 §4 used for
    /// second-order confirmations (e.g. <c>Remove-Item -Recurse -Force</c> after
    /// <see cref="ShouldProcess"/> already passed).
    /// </summary>
    /// <param name="target">Human-readable description of the operation target.</param>
    /// <param name="action">Human-readable description of the operation action.</param>
    /// <returns><c>true</c> to proceed; <c>false</c> to skip the action.</returns>
    bool ShouldContinue(string target, string action);

    /// <summary>Whether this call is running in WhatIf (dry-run) mode.</summary>
    bool WhatIfPreference { get; }

    /// <summary>Current confirm-preference threshold (None/Low/Medium/High). Defaults to High.</summary>
    ConfirmPreference ConfirmPreference { get; }

    /// <summary>
    /// Reset the session-level YesToAll / NoToAll state. Called by the host between
    /// command invocations so each command starts with a fresh prompt budget.
    /// Per ADR-0049 §3.2: session state is command-call scoped.
    /// </summary>
    void ResetSessionConfirmState();
}

/// <summary>
/// User-tunable confirm threshold. Per ADR-0049 §5. Ordered
/// <c>None &lt; Low &lt; Medium &lt; High</c>. An action with <c>ConfirmImpact</c>
/// greater than or equal to the current <see cref="ConfirmPreference"/>
/// triggers an interactive prompt (unless YesToAll is already set).
/// </summary>
public enum ConfirmPreference
{
    /// <summary>Disable all auto-confirm prompts.</summary>
    None = 0,

    /// <summary>Prompt for Low / Medium / High impact actions.</summary>
    Low = 1,

    /// <summary>Prompt for Medium / High impact actions.</summary>
    Medium = 2,

    /// <summary>Prompt only for High impact actions. PowerShell default.</summary>
    High = 3,
}
