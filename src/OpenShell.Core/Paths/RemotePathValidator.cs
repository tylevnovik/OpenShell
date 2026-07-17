namespace OpenShell.Paths;

/// <summary>
/// 远程路径安全校验器。Per ADR-0034 §6.
/// 拒绝含嵌入空字节或超出根目录的路径遍历, 防止恶意路径注入与目录逃逸。
/// 用于远程 Provider (SFTP / WebDAV 等) 在执行文件操作前校验路径合法性。
/// </summary>
/// <remarks>
/// 校验规则:
/// <list type="bullet">
///   <item><b>空字节</b>: 路径含 <c>'\0'</c> 时拒绝 (C 字符串截断攻击)。</item>
///   <item><b>路径遍历</b>: 规范化后路径以 <c>../</c> 开头或含跳出根目录的 <c>..</c> 段时拒绝。</item>
/// </list>
/// 校验失败抛 <see cref="ArgumentException"/>; 调用方应捕获并映射为 <c>ErrorRecord</c>。
/// </remarks>
public static class RemotePathValidator
{
    /// <summary>
    /// 校验远程路径安全性。Per ADR-0034 §6.
    /// </summary>
    /// <param name="remotePath">远程路径 (已解析出的 internal path 部分, 如 <c>/home/user/file</c>)。</param>
    /// <exception cref="ArgumentException">路径含空字节或路径遍历超出根目录。</exception>
    public static void Validate(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
            return; // 空路径由调用方处理 (如默认到根目录)。

        // 1. 拒绝嵌入空字节 (防止 C 字符串截断绕过校验)。
        if (remotePath.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "Remote path contains embedded null byte, which is not allowed for security reasons.",
                nameof(remotePath));
        }

        // 2. 拒绝路径遍历超出根目录。
        // 规范化: 折叠 "." 与 ".." 段, 若结果仍以 ".." 开头则说明试图逃逸根目录。
        var normalized = NormalizeForValidation(remotePath);
        if (normalized.StartsWith("..", StringComparison.Ordinal) || normalized.StartsWith("/..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Remote path traversal beyond root is not allowed: '{remotePath}' (normalized: '{normalized}').",
                nameof(remotePath));
        }
    }

    /// <summary>
    /// 校验并返回路径是否安全。Per ADR-0034 §6. 不抛异常版本。
    /// </summary>
    /// <returns>true = 安全; false = 含空字节或路径遍历。</returns>
    public static bool IsValid(string remotePath)
    {
        try
        {
            Validate(remotePath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 规范化路径用于安全校验: 折叠 "." 与 ".." 段。
    /// 与 Provider 内部的 NormalizeRemotePath 逻辑一致, 但仅用于判断是否逃逸根目录。
    /// </summary>
    private static string NormalizeForValidation(string remotePath)
    {
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                else
                    stack.Add(".."); // 根目录之上的 ".." 保留, 用于检测逃逸。
                continue;
            }
            stack.Add(seg);
        }
        return string.Join('/', stack);
    }
}
