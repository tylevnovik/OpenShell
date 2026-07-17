namespace OpenShell.Security;

/// <summary>
/// 控制台密码提示器。Per ADR-0036 §14.
/// 简化实现: 直接 <see cref="Console.ReadLine"/> 读取 (明文可见), 适用于无 GUI 的 CLI 场景。
/// </summary>
/// <remarks>
/// TODO(ADR-0036 §14): OS 原生安全输入 (Windows CredUI / macOS Security.framework / Unix getpass)
/// 应替换此实现, 避免密码明文回显。当前实现仅作占位, 满足 paranoid 模式基本流程。
/// </remarks>
public sealed class ConsoleSecurePasswordPrompter : ISecurePasswordPrompter
{
    /// <inheritdoc />
    public Task<string?> PromptPasswordAsync(string prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Console.IsInputRedirected)
        {
            // 交互式终端: 输出提示 + 明文可见警告。
            Console.Error.Write(prompt + " ");
            Console.Error.WriteLine("(input will be visible — OS-native secure input not yet implemented)");
        }

        try
        {
            var line = Console.ReadLine();
            return Task.FromResult(line);
        }
        catch (IOException)
        {
            // stdin 不可读 (无控制台) → 视为取消。
            return Task.FromResult<string?>(null);
        }
    }
}
