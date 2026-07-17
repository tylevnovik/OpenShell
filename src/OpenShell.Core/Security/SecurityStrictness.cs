namespace OpenShell.Security;

/// <summary>
/// 用户可配置严格度。Per ADR-0036 §14.
/// 决定 <see cref="SecurityService.ConfirmAsync"/> 在何种风险等级下需要二次确认。
/// </summary>
public enum SecurityStrictness
{
    /// <summary>宽松: 仅 Destructive 需确认。</summary>
    Lax,

    /// <summary>默认: Critical 及以上需确认。</summary>
    Default,

    /// <summary>严格: High 及以上需确认。</summary>
    Strict,

    /// <summary>偏执: Medium 及以上需确认。</summary>
    Paranoid,
}
