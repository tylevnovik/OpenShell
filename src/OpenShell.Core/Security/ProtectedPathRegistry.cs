using OpenShell.Paths;

namespace OpenShell.Security;

/// <summary>
/// 受保护路径注册表。Per ADR-0036 §4.
/// 默认值包含各平台系统目录; 可通过 <see cref="Add"/> 动态扩展 (来自 config.toml 的 [security].protectedPaths)。
/// 受保护路径的写/删操作需 <c>--force</c> 并记录审计。
/// </summary>
public sealed class ProtectedPathRegistry
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造 ProtectedPathRegistry。默认填充当前操作系统的系统目录。</summary>
    /// <param name="initialPaths">可选初始路径集合 (来自配置文件)。null 时仅使用内置默认值。</param>
    public ProtectedPathRegistry(IEnumerable<string>? initialPaths = null)
    {
        // 内置默认: 当前操作系统系统目录 (大小写不敏感 set 自动去重)。
        foreach (var p in GetDefaultProtectedPaths())
            _paths.Add(Normalize(p));

        if (initialPaths is not null)
        {
            foreach (var p in initialPaths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    _paths.Add(Normalize(p));
            }
        }
    }

    /// <summary>判断路径是否在受保护路径下 (前缀匹配, 大小写不敏感)。</summary>
    public bool IsProtected(ItemPath path)
    {
        var normalized = Normalize(path.Display);
        if (normalized.Length == 0) return false;

        lock (_paths)
        {
            foreach (var protectedPath in _paths)
            {
                // 精确匹配 或 受保护路径是该路径的前缀 (路径分隔符 '/').
                if (string.Equals(normalized, protectedPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (normalized.Length > protectedPath.Length
                    && normalized.StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase)
                    && normalized[protectedPath.Length] == '/')
                    return true;
            }
        }
        return false;
    }

    /// <summary>添加一条受保护路径 (大小写不敏感, 自动归一化)。</summary>
    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_paths)
        {
            _paths.Add(Normalize(path));
        }
    }

    /// <summary>移除一条受保护路径 (大小写不敏感)。</summary>
    public void Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_paths)
        {
            _paths.Remove(Normalize(path));
        }
    }

    /// <summary>返回当前所有受保护路径的快照 (供测试 / 调试)。</summary>
    public IReadOnlyCollection<string> List()
    {
        lock (_paths)
        {
            return _paths.ToArray();
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        // 统一分隔符 '\' -> '/' 并去尾部 '/'.
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized;
    }

    /// <summary>当前操作系统对应的默认受保护路径集合 (provider::internal 形式)。</summary>
    private static IEnumerable<string> GetDefaultProtectedPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "fs::C:/Windows";
            yield return "fs::C:/Program Files";
            yield return "fs::C:/Program Files (x86)";
            yield return "fs::C:/ProgramData";
            yield return "reg::HKLM/SAM";
            yield return "reg::HKLM/SECURITY";
        }
        else
        {
            yield return "fs::/etc";
            yield return "fs::/usr";
            yield return "fs::/bin";
            yield return "fs::/sbin";
            yield return "fs::/var";
            yield return "fs::/boot";
            yield return "fs::/root";
            yield return "fs::/lib";
            yield return "fs::/lib64";
        }
    }
}
