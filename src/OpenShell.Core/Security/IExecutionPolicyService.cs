using OpenShell.Packaging.Signing;

namespace OpenShell.Security;

/// <summary>
/// 脚本执行策略服务。Per ADR-0054.
/// 负责读取 / 设置 ExecutionPolicy, 检查脚本是否可执行, 校验签名。
/// </summary>
public interface IExecutionPolicyService
{
    /// <summary>获取当前有效策略 (Process > User > Machine)。Per ADR-0054 §2.</summary>
    ExecutionPolicy GetEffectivePolicy();

    /// <summary>获取指定 scope 的策略 (未配置返回 null)。</summary>
    ExecutionPolicy? GetPolicy(ExecutionPolicyScope scope);

    /// <summary>设置指定 scope 的策略。Machine scope 需管理员权限。</summary>
    void SetPolicy(ExecutionPolicy policy, ExecutionPolicyScope scope);

    /// <summary>列出所有 scope 的策略 (用于 Get-ExecutionPolicy -List)。</summary>
    IReadOnlyDictionary<ExecutionPolicyScope, ExecutionPolicy?> ListScopes();

    /// <summary>
    /// 检查脚本文件是否可执行。Per ADR-0054 §5.
    /// 返回 (canExecute, reason), reason 用于错误信息与审计日志。
    /// </summary>
    /// <param name="filePath">脚本文件绝对路径。</param>
    /// <param name="isRemote">是否为远程文件 (调用方预先判断, 通常用 <see cref="IsRemoteFile"/>)。</param>
    (bool canExecute, string reason) CanExecute(string filePath, bool isRemote);

    /// <summary>
    /// 检查文件是否为远程来源。Per ADR-0054 §3.
    /// Windows: 读取 Zone.Identifier ADS, ZoneId >= 3 视为远程。
    /// Unix: 读取 xattr (user.xdg.origin.url / user.openshell.remote / com.apple.quarantine)。
    /// </summary>
    bool IsRemoteFile(string filePath);

    /// <summary>
    /// 校验脚本文件的 Ed25519 签名。Per ADR-0054 §4.
    /// 读取 &lt;script&gt;.sig + &lt;script&gt;.pub, 复用 <see cref="ISignatureVerifier"/>。
    /// </summary>
    Task<SignatureResult> CheckSignatureAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// ExecutionPolicy 决策结果。Per ADR-0054 §5.
/// </summary>
public readonly record struct ExecutionPolicyDecision(bool CanExecute, string Reason)
{
    public static ExecutionPolicyDecision Allow(string reason = "allowed") => new(true, reason);
    public static ExecutionPolicyDecision Deny(string reason) => new(false, reason);
}
