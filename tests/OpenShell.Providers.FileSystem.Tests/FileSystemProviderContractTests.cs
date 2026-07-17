using OpenShell.Paths;
using OpenShell.Providers.FileSystem;
using OpenShell.TestUtils;
using OpenShell.TestUtils.Contract;
using Xunit;

namespace OpenShell.Providers.FileSystem.Tests;

/// <summary>
/// FileSystemProvider 契约测试。Per ADR-0001, ADR-0033.
/// 继承 ProviderContractTests, 自动覆盖 Info / Capabilities / InitialiseAsync / GetItem / GetChildren / cancellation 契约。
/// 每个测试用 TempDir 隔离文件系统。
/// </summary>
public class FileSystemProviderContractTests : ProviderContractTests<FileSystemProvider>, IDisposable
{
    private readonly TempDir _tempDir = new();

    protected override FileSystemProvider CreateProvider() => new();

    protected override ItemPath GetTestRoot()
    {
        // 返回 fs:: + 临时目录路径 (内部路径用 '/' 分隔)。
        return new ItemPath
        {
            Provider = "fs",
            InternalPath = _tempDir.FullPath.Replace('\\', '/'),
        };
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
