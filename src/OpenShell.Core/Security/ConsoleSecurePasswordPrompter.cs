using System.Text;

namespace OpenShell.Security;

/// <summary>
/// 控制台密码提示器。Per ADR-0036 §14.
/// 交互式终端使用 <see cref="Console.ReadKey(bool)"/> 截获输入，不回显密码；重定向 stdin 时保留管道兼容性。
/// </summary>
/// <remarks>
/// 非 TTY 场景无法控制外部输入设备，因此使用 ReadLine；调用方应避免在共享终端传入密码。
/// </remarks>
public sealed class ConsoleSecurePasswordPrompter : ISecurePasswordPrompter
{
    private readonly Func<ConsoleKeyInfo> _readKey;
    private readonly Func<string?> _readLine;
    private readonly Func<bool> _isInputRedirected;
    private readonly TextWriter _error;

    public ConsoleSecurePasswordPrompter(
        Func<ConsoleKeyInfo>? readKey = null,
        Func<string?>? readLine = null,
        Func<bool>? isInputRedirected = null,
        TextWriter? error = null)
    {
        _readKey = readKey ?? (() => Console.ReadKey(intercept: true));
        _readLine = readLine ?? (() => Console.ReadLine());
        _isInputRedirected = isInputRedirected ?? (() => Console.IsInputRedirected);
        _error = error ?? Console.Error;
    }

    /// <inheritdoc />
    public Task<string?> PromptPasswordAsync(string prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_isInputRedirected())
        {
            try { return Task.FromResult(_readLine()); }
            catch (IOException) { return Task.FromResult<string?>(null); }
        }

        _error.Write(prompt + " ");
        var password = new StringBuilder();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var key = _readKey();
                if (key.Key == ConsoleKey.Enter)
                {
                    _error.WriteLine();
                    return Task.FromResult<string?>(password.ToString());
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0) password.Length--;
                    continue;
                }
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    _error.WriteLine();
                    return Task.FromResult<string?>(null);
                }
                if (!char.IsControl(key.KeyChar))
                    password.Append(key.KeyChar);
            }
        }
        catch (IOException)
        {
            // stdin 不可读 (无控制台) → 视为取消。
            return Task.FromResult<string?>(null);
        }
    }
}
