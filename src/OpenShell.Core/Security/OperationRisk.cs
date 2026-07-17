namespace OpenShell.Security;

/// <summary>
/// 操作风险等级。Per ADR-0036 §1.
/// </summary>
public enum OperationRisk
{
    /// <summary>只读操作: get-*, list-*。</summary>
    Safe,

    /// <summary>低风险: copy/move 普通文件。</summary>
    Low,

    /// <summary>中等风险: new-item, set-property。</summary>
    Medium,

    /// <summary>高风险: remove-item, set-content 系统文件。</summary>
    High,

    /// <summary>严重风险: remove-item -r 根目录, 远程上传大文件。</summary>
    Critical,

    /// <summary>破坏性: remove-item -force 物理删除, 注册表 HKLM 写入。</summary>
    Destructive,
}
