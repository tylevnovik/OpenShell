using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// 用户挂载的虚拟 Drive 注册表。Per ADR-0023 (M1 命令清单 New-PSDrive / Remove-PSDrive).
/// 与 <see cref="IDriveProvider.GetDrivesAsync"/> 不同：本注册表维护用户通过 New-PSDrive
/// 显式挂载的虚拟 Drive（命名快捷方式），不依赖具体 Provider 上报。
/// </summary>
public interface IDriveRegistry
{
    /// <summary>所有已挂载的虚拟 Drive（按挂载时间排序）。</summary>
    IReadOnlyList<ProviderDrive> Mounted { get; }

    /// <summary>挂载一个虚拟 Drive。同名覆盖。</summary>
    void Mount(ProviderDrive drive);

    /// <summary>按名卸载。返回是否找到并卸载。</summary>
    bool Unmount(string name);

    /// <summary>按名查找。</summary>
    ProviderDrive? Find(string name);

    /// <summary>挂载/卸载事件。</summary>
    event EventHandler<ProviderDrive>? MountedChanged;
}
