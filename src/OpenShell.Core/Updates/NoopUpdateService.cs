using System.Reactive.Subjects;

namespace OpenShell.Updates;

/// <summary>
/// 空实现的 <see cref="IUpdateService"/>。Per ADR-0037.
/// 用于离线 / 测试环境: <see cref="CheckForUpdatesAsync"/> 返回 null,
/// <see cref="DownloadAsync"/> / <see cref="InstallAsync"/> 抛 <see cref="NotSupportedException"/>。
/// </summary>
public sealed class NoopUpdateService : IUpdateService
{
    private readonly Subject<UpdateStatus> _status = new();

    /// <inheritdoc />
    public ValueTask<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<UpdateInfo?>((UpdateInfo?)null);
    }

    /// <inheritdoc />
    public ValueTask DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Auto-update is not available (NoopUpdateService). Configure a real IUpdateService or run in an online environment.");
    }

    /// <inheritdoc />
    public ValueTask InstallAsync(UpdateInfo info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Auto-update is not available (NoopUpdateService). Configure a real IUpdateService or run in an online environment.");
    }

    /// <inheritdoc />
    public ValueTask InstallFromOfflineAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Offline package path is required.", nameof(path));
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Offline update is not available (NoopUpdateService). Configure a real IUpdateService or run in an online environment.");
    }

    /// <inheritdoc />
    public IObservable<UpdateStatus> StatusChanged => _status;
}
