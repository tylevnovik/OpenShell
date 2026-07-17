namespace OpenShell.Security;

/// <summary>
/// 脚本执行策略级别。Per ADR-0054 §1.
/// 与 PowerShell 的 ExecutionPolicy 对齐 (但不含 AllSigned / Default, 见 ADR-0054 §1 备注)。
/// </summary>
public enum ExecutionPolicy
{
    /// <summary>
    /// 禁止执行任何脚本 (.osh / .ps1), 仅允许交互式 REPL。
    /// Per ADR-0054 §1: 等价 PowerShell Restricted。
    /// </summary>
    Restricted,

    /// <summary>
    /// 本地脚本自由执行; 远程 (下载的) 脚本需 Ed25519 数字签名。
    /// Per ADR-0054 §1: 默认策略, 等价 PowerShell RemoteSigned (Linux/macOS 默认值)。
    /// </summary>
    RemoteSigned,

    /// <summary>
    /// 所有脚本可执行, 但远程脚本会弹确认提示。
    /// Per ADR-0054 §1: 等价 PowerShell Unrestricted。
    /// </summary>
    Unrestricted,

    /// <summary>
    /// 无任何限制 (CI/CD 场景)。不进行远程检测 / 签名校验 / 确认提示。
    /// Per ADR-0054 §1: 等价 PowerShell Bypass。
    /// </summary>
    Bypass,
}

/// <summary>
/// ExecutionPolicy 的作用域级别。Per ADR-0054 §2.
/// 优先级: Process > User > Machine。
/// </summary>
public enum ExecutionPolicyScope
{
    /// <summary>系统级 (Windows: HKLM; Unix: /etc/openshell/policy.toml)。需管理员权限修改。</summary>
    Machine,

    /// <summary>用户级 (~/.openshell/config.toml 的 executionPolicy 字段)。</summary>
    User,

    /// <summary>进程级 (-ExecutionPolicy CLI flag / 环境变量)。退出即失效。</summary>
    Process,
}
