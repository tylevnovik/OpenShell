namespace OpenShell.Security;

/// <summary>
/// 声明命令所需的权限级别。Per ADR-0036 §8.
/// 启动时根据 config.toml 的用户角色映射命令级别。
/// </summary>
/// <example>
/// <code>
/// [Verb("Remove", Noun = "Item")]
/// [RequiredPermission(PermissionLevel.High)]
/// public sealed class RemoveItemCommand : ...
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequiredPermissionAttribute : Attribute
{
    public PermissionLevel Level { get; }

    public RequiredPermissionAttribute(PermissionLevel level)
    {
        Level = level;
    }
}
