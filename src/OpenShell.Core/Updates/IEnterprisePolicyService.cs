namespace OpenShell.Updates;

/// <summary>
/// 企业策略服务抽象。Per ADR-0037 §12.
/// 读取 OS 特定策略文件 (<c>%ProgramData%\OpenShell\policy.toml</c> / <c>/etc/openshell/policy.toml</c>),
/// 暴露更新禁用开关与目标版本锁定。
/// </summary>
public interface IEnterprisePolicyService
{
    /// <summary>策略文件是否存在 (false 时所有属性均为默认值)。</summary>
    bool IsPolicyFilePresent { get; }

    /// <summary>是否禁用自动更新。true 时 <see cref="IUpdateService"/> 应替换为 Noop。</summary>
    bool UpdatesEnabled { get; }

    /// <summary>锁定目标版本 (null 表示无锁定, 允许任意新版本)。</summary>
    string? TargetVersion { get; }
}
