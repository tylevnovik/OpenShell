namespace OpenShell.Security;

/// <summary>
/// 命令权限级别。Per ADR-0036 §8.
/// 启动时根据 config.toml 的用户角色映射命令级别。
/// </summary>
public enum PermissionLevel
{
    /// <summary>只读, 无需确认。</summary>
    ReadOnly,

    /// <summary>写普通文件。</summary>
    Low,

    /// <summary>写系统文件、注册表。</summary>
    High,

    /// <summary>物理删除、远程批量。</summary>
    Critical,
}
