using System.IO;
using OpenShell.Paths;

namespace OpenShell.Security;

/// <summary>
/// 操作风险分析器。Per ADR-0036 §3.
/// 纯逻辑类, 无 IO 依赖; <see cref="GetItemCount"/> 简化为返回 -1 (无法判断时不升级到 Critical)。
/// </summary>
public class RiskAnalyzer
{
    private const int LargeBatchThreshold = 1000;

    // Windows 受保护目录 (内部路径用 '/' 分隔)。匹配时大小写不敏感。
    private static readonly string[] WindowsSystemDirectories =
    {
        "C:/Windows",
        "C:/Program Files",
        "C:/Program Files (x86)",
        "C:/ProgramData",
        "C:/Windows/System32",
        "C:/Windows/SysWOW64",
    };

    // Unix 受保护目录。
    private static readonly string[] UnixSystemDirectories =
    {
        "/etc",
        "/usr",
        "/bin",
        "/sbin",
        "/var",
        "/boot",
        "/root",
        "/lib",
        "/lib64",
    };

    /// <summary>
    /// 分析命令的风险等级。Per ADR-0036 §3.
    /// </summary>
    /// <param name="command">命令名 (小写, 如 "remove-item")。</param>
    /// <param name="path">目标路径, 可为 null (如命令未指定路径)。</param>
    /// <param name="force">是否带 --force (用于 remove-item 物理删除不走 Trash)。</param>
    /// <param name="recurse">是否递归 (用于影响大批量判断; 简化版未直接使用)。</param>
    /// <param name="useTrash">是否走 Trash (Recycle Bin); false 表示物理删除。</param>
    public OperationRisk Analyze(string command, ItemPath? path, bool force, bool recurse, bool useTrash)
    {
        if (string.IsNullOrEmpty(command)) return OperationRisk.Low;
        var cmd = command.ToLowerInvariant().Trim();
        return cmd switch
        {
            "remove-item" or "rm" or "del" or "ri" => AnalyzeRemove(path, force, useTrash),
            "copy-item" or "cp" or "ci" => AnalyzeCopy(path),
            "set-content" or "sc" => AnalyzeSetContent(path),
            _ => OperationRisk.Low,
        };
    }

    /// <summary>
    /// 分析 remove-item 风险: 根目录/系统目录 → Critical; 大批量 (>1000) → Critical;
    /// --force 不走 Trash → Destructive; 否则 High。
    /// </summary>
    private OperationRisk AnalyzeRemove(ItemPath? path, bool force, bool useTrash)
    {
        if (path is null) return OperationRisk.Low;
        var p = path.Value;

        if (IsRoot(p) || IsSystemDirectory(p))
            return OperationRisk.Critical;

        // ADR-0036 §13: 隐藏文件 (且未 --force) → Critical (防止误删隐藏配置/系统文件)。
        if (!force && HasHiddenAttribute(p))
            return OperationRisk.Critical;

        var itemCount = GetItemCount(p);
        if (itemCount > LargeBatchThreshold)
            return OperationRisk.Critical;

        // --force 且不走 Trash → 物理删除, Destructive。
        if (force && !useTrash)
            return OperationRisk.Destructive;

        return OperationRisk.High;
    }

    /// <summary>
    /// 分析 copy-item 风险: 跨 Provider → Medium; 大批量 → High; 否则 Low。
    /// </summary>
    /// <remarks>
    /// 本简化版仅凭 path 判断; 实际跨 Provider 需源 + 目标路径。这里若路径非 fs Provider 即视为跨 Provider。
    /// </remarks>
    private OperationRisk AnalyzeCopy(ItemPath? path)
    {
        if (path is null) return OperationRisk.Low;
        var p = path.Value;

        // 非 fs 视为跨 Provider (s3 / remote / zip 等)。
        if (!string.Equals(p.Provider, "fs", StringComparison.Ordinal))
            return OperationRisk.Medium;

        var itemCount = GetItemCount(p);
        if (itemCount > LargeBatchThreshold)
            return OperationRisk.High;

        return OperationRisk.Low;
    }

    /// <summary>
    /// 分析 set-content 风险: 系统文件 → High; 否则 Medium。
    /// </summary>
    private OperationRisk AnalyzeSetContent(ItemPath? path)
    {
        if (path is null) return OperationRisk.Medium;
        var p = path.Value;

        if (IsSystemDirectory(p) || IsInProtectedPath(p))
            return OperationRisk.High;

        // ADR-0036 §13: 系统属性文件 → High (防止覆盖 pagefile.sys / hiberfil.sys 等)。
        if (HasSystemAttribute(p))
            return OperationRisk.High;

        return OperationRisk.Medium;
    }

    /// <summary>判断路径是否为根目录。Per ADR-0036 §3.</summary>
    public bool IsRoot(ItemPath path)
    {
        var internalPath = path.InternalPath;
        if (string.IsNullOrEmpty(internalPath)) return false;
        // "/" 或 "C:/" 这种形式视为根。
        if (internalPath == "/") return true;
        // Windows 盘符根 "C:/" "D:/"。
        if (internalPath.Length == 3
            && char.IsLetter(internalPath[0])
            && internalPath[1] == ':'
            && internalPath[2] == '/')
            return true;
        return false;
    }

    /// <summary>判断路径是否在系统目录下。Per ADR-0036 §3.</summary>
    public bool IsSystemDirectory(ItemPath path)
    {
        // 只判断 fs Provider (其他 Provider 的根目录由 Provider 自行保护)。
        if (!string.Equals(path.Provider, "fs", StringComparison.Ordinal))
            return false;

        var internalPath = path.InternalPath;
        if (string.IsNullOrEmpty(internalPath)) return false;

        // 归一化: 去掉尾部 '/' 以便前缀匹配。
        var normalized = internalPath.TrimEnd('/');
        if (normalized.Length == 0) return false;

        foreach (var sysDir in GetSystemDirectoriesForCurrentOS())
        {
            // 大小写不敏感比较 (Windows); Unix 路径本身大小写敏感, 但小写表恒等。
            if (string.Equals(normalized, sysDir, StringComparison.OrdinalIgnoreCase))
                return true;
            // 子路径前缀匹配: "C:/Windows/System32/drivers" 在 "C:/Windows" 下。
            if (normalized.Length > sysDir.Length
                && normalized.StartsWith(sysDir, StringComparison.OrdinalIgnoreCase)
                && normalized[sysDir.Length] == '/')
                return true;
        }
        return false;
    }

    /// <summary>判断路径是否在受保护路径列表中 (默认系统目录集合)。</summary>
    private bool IsInProtectedPath(ItemPath path)
        => IsSystemDirectory(path);

    /// <summary>
    /// 获取路径下条目数。Per ADR-0036 §3 简化版: 永远返回 -1 (无法判断时不升级到 Critical)。
    /// 实际实现需 IO 调用; 测试可注入子类重写。
    /// </summary>
    protected virtual int GetItemCount(ItemPath path) => -1;

    /// <summary>当前操作系统对应的系统目录集合。</summary>
    private static string[] GetSystemDirectoriesForCurrentOS()
        => OperatingSystem.IsWindows() ? WindowsSystemDirectories : UnixSystemDirectories;

    /// <summary>
    /// 判断路径指向的文件是否具有 <see cref="FileAttributes.Hidden"/> 属性 (仅 fs Provider)。Per ADR-0036 §13.
    /// 文件不存在或读取失败时返回 false (保守不升级风险)。
    /// </summary>
    public bool HasHiddenAttribute(ItemPath path)
        => TryGetFileAttributes(path, out var attrs) && (attrs & FileAttributes.Hidden) != 0;

    /// <summary>
    /// 判断路径指向的文件是否具有 <see cref="FileAttributes.System"/> 属性 (仅 fs Provider)。Per ADR-0036 §13.
    /// 文件不存在或读取失败时返回 false。
    /// </summary>
    public bool HasSystemAttribute(ItemPath path)
        => TryGetFileAttributes(path, out var attrs) && (attrs & FileAttributes.System) != 0;

    /// <summary>
    /// 安全读取 fs Provider 路径的 <see cref="FileAttributes"/>。Per ADR-0036 §13.
    /// 非 fs Provider 或文件不存在 / IO 异常 / 权限不足时返回 false。
    /// </summary>
    private static bool TryGetFileAttributes(ItemPath path, out FileAttributes attrs)
    {
        attrs = default;
        if (!string.Equals(path.Provider, "fs", StringComparison.Ordinal)) return false;
        var internalPath = path.InternalPath;
        if (string.IsNullOrEmpty(internalPath)) return false;
        try
        {
            attrs = File.GetAttributes(ToNativePath(internalPath));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>将 InternalPath ('/' 分隔) 转换为当前 OS 原生路径分隔符 (供 <see cref="File"/> API 使用)。</summary>
    private static string ToNativePath(string internalPath)
        => Path.DirectorySeparatorChar == '/'
            ? internalPath
            : internalPath.Replace('/', Path.DirectorySeparatorChar);
}
