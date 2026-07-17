using System.Collections.Concurrent;
using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// 默认 <see cref="IDriveRegistry"/> 内存实现。Per ADR-0023.
/// 线程安全；Drive 不持久化（重启清空），如需持久化用 ADR-0022 启动脚本（ADR-0041）。
/// </summary>
public sealed class InMemoryDriveRegistry : IDriveRegistry
{
    private readonly ConcurrentDictionary<string, ProviderDrive> _drives = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _eventLock = new();
    private EventHandler<ProviderDrive>? _mountedChanged;

    public IReadOnlyList<ProviderDrive> Mounted => _drives.Values.ToList();

    public event EventHandler<ProviderDrive>? MountedChanged
    {
        add { lock (_eventLock) _mountedChanged += value; }
        remove { lock (_eventLock) _mountedChanged -= value; }
    }

    public void Mount(ProviderDrive drive)
    {
        if (string.IsNullOrWhiteSpace(drive.Name))
            throw new ArgumentException("Drive name cannot be empty.", nameof(drive));
        _drives[drive.Name] = drive;
        _mountedChanged?.Invoke(this, drive);
    }

    public bool Unmount(string name)
    {
        if (!_drives.TryRemove(name, out var drive)) return false;
        _mountedChanged?.Invoke(this, drive);
        return true;
    }

    public ProviderDrive? Find(string name)
        => _drives.TryGetValue(name, out var drive) ? drive : null;
}
