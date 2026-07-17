namespace OpenShell.Security;

/// <summary>
/// 安全密码提示器抽象。Per ADR-0036 §14.
/// paranoid 模式下对 Critical / Destructive 操作要求用户输入 PIN 二次确认。
/// 实现应使用 OS 原生安全输入 (Windows CredUI / macOS Security.framework / Unix getpass), 避免明文回显。
/// </summary>
public interface ISecurePasswordPrompter
{
    /// <summary>
    /// 提示用户输入密码 (PIN)。
    /// </summary>
    /// <param name="prompt">提示文本 (如 "Enter PIN to confirm destructive operation:")。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>用户输入的密码; null 表示用户取消或输入不可读。</returns>
    Task<string?> PromptPasswordAsync(string prompt, CancellationToken ct = default);
}
