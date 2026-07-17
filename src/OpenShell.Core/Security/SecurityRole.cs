namespace OpenShell.Security;

/// <summary>
/// 用户角色。Per ADR-0036 §9.
/// 决定 <see cref="SecurityService.IsAllowed"/> 是否允许 High 及以上操作。
/// </summary>
public enum SecurityRole
{
    /// <summary>默认, 遵守风险等级确认策略。</summary>
    User,

    /// <summary>管理员, 跳过 High 及以下确认。</summary>
    Admin,

    /// <summary>受限用户, 禁止 High 及以上操作。</summary>
    Restricted,
}
