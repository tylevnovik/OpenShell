using System.Text.RegularExpressions;

namespace OpenShell.Security;

/// <summary>
/// 凭据脱敏器。Per ADR-0036 §10.
/// 将命令行中敏感 flag (<c>--secret</c> / <c>--password</c> / <c>--token</c> / <c>--apikey</c> /
/// <c>--key</c> / <c>-p</c>, 大小写不敏感) 后的值替换为 <c>***REDACTED***</c>,
/// 防止凭据泄漏到审计日志。同时提供命令名是否为凭据相关命令的判断 (用于历史记录排除)。
/// </summary>
public static class CredentialRedactor
{
    /// <summary>脱敏后的占位符。</summary>
    public const string RedactedPlaceholder = "***REDACTED***";

    // 敏感 flag (大小写不敏感)。匹配 flag 后紧跟 = 或 空白, 再跟一个值 (引号字符串或非空白 token)。
    // (?<=^|\s) 确保 flag 位于行首或空白后, 避免误匹配子串 (如 -profile 中的 -p)。
    // 值部分接受 "..." 引号字符串 (允许含空格的密码) 或 \S+ 非空白 token。
    private static readonly Regex CredentialFlagPattern = new(
        @"(?<=^|\s)(--secret|--password|--token|--apikey|--key|-p)(\s*=\s*|\s+)(""[^""]*""|\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    // 凭据相关命令名词 (大小写不敏感)。ShouldExcludeFromHistory 用。
    // Per ADR-0036 §10: 凭据设置/移除命令不应进入历史记录。
    private static readonly HashSet<string> CredentialCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "set-sftpcredential",
        "remove-sftpcredential",
        "set-credential",
        "remove-credential",
    };

    /// <summary>
    /// 脱敏命令行中敏感 flag 后的值。
    /// 示例: <c>set-sftpcredential --secret mypassword</c> → <c>set-sftpcredential --secret ***REDACTED***</c>。
    /// </summary>
    /// <param name="args">原始命令行参数字符串; null 视为空字符串。</param>
    /// <returns>脱敏后的字符串; 超时降级返回原文 (best-effort, 不阻塞审计写入)。</returns>
    public static string Redact(string args)
    {
        if (string.IsNullOrEmpty(args)) return args ?? string.Empty;
        try
        {
            return CredentialFlagPattern.Replace(args, m =>
                m.Groups[1].Value + m.Groups[2].Value + RedactedPlaceholder);
        }
        catch (RegexMatchTimeoutException)
        {
            // 超时降级: 返回原文 (best-effort, 不阻塞审计写入)。
            return args;
        }
    }

    /// <summary>
    /// 判断命令行是否为凭据相关命令 (应从历史记录中排除)。Per ADR-0036 §10.
    /// 取命令行第一个 token 作为命令名, 与凭据命令集合比对; 同时匹配 <c>*-credential</c> /
    /// <c>*-sftpcredential</c> 后缀模式。
    /// </summary>
    /// <param name="commandLine">完整命令行; null 或空白返回 false。</param>
    public static bool ShouldExcludeFromHistory(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        var firstToken = ExtractFirstToken(commandLine);
        if (string.IsNullOrEmpty(firstToken)) return false;
        if (CredentialCommands.Contains(firstToken)) return true;
        var lower = firstToken.ToLowerInvariant();
        return lower.EndsWith("-credential", StringComparison.Ordinal)
            || lower.EndsWith("-sftpcredential", StringComparison.Ordinal);
    }

    private static string ExtractFirstToken(string commandLine)
    {
        var span = commandLine.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t');
        return end < 0 ? span.ToString() : span[..end].ToString();
    }
}
