namespace OpenShell.TestUtils;

/// <summary>
/// 临时目录隔离器。Per ADR-0033: 集成测试必须在隔离的临时目录中运行。
/// 构造时创建随机子目录，Dispose 时递归删除。
/// 用法：using var dir = new TempDir();
/// </summary>
public sealed class TempDir : IDisposable
{
    private readonly string _path;
    private bool _disposed;

    /// <summary>构造：在系统临时目录下创建一个随机命名的子目录。</summary>
    public TempDir()
    {
        _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openshell-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_path);
    }

    /// <summary>临时目录的绝对路径。</summary>
    public string FullPath => _path;

    /// <summary>在临时目录下创建一个文件并写入内容。父目录会自动创建。</summary>
    public string CreateFile(string relativePath, string? content = null)
    {
        var full = System.IO.Path.Combine(_path, relativePath);
        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(full, content ?? string.Empty);
        return full;
    }

    /// <summary>在临时目录下创建一个子目录（含必要的中间目录）。</summary>
    public string CreateDirectory(string relativePath)
    {
        var full = System.IO.Path.Combine(_path, relativePath);
        System.IO.Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>获取临时目录下某相对路径的完整路径。</summary>
    public string GetFullPath(string relativePath)
        => System.IO.Path.Combine(_path, relativePath);

    /// <summary>判断临时目录下某文件是否存在。</summary>
    public bool FileExists(string relativePath)
        => System.IO.File.Exists(GetFullPath(relativePath));

    /// <summary>判断临时目录下某目录是否存在。</summary>
    public bool DirectoryExists(string relativePath)
        => System.IO.Directory.Exists(GetFullPath(relativePath));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (System.IO.Directory.Exists(_path))
                System.IO.Directory.Delete(_path, recursive: true);
        }
        catch
        {
            // best-effort: 不抛异常避免影响测试结果。
        }
    }
}
