using OpenShell.Errors;

namespace OpenShell.Security;

/// <summary>
/// 沙箱违规异常。Per ADR-0036 §11 / §12.
/// 当第三方 Provider 沙箱拒绝网络访问 (§11) 或进程生成 (§12) 时抛出。
/// 属于 <see cref="ErrorCategory.PermissionDenied">权限拒绝</see> 类别错误。
/// </summary>
public sealed class SecuritySandboxViolationException : OpenShellException
{
    public SecuritySandboxViolationException(string message) : base(message) { }

    public SecuritySandboxViolationException(string message, Exception innerException) : base(message, innerException) { }

    public override ErrorCategory Category => ErrorCategory.PermissionDenied;
}
