using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Commands;

namespace OpenShell;

/// <summary>
/// Per ADR-design: host abstraction shared by CLI and GUI.
/// Both hosts implement this so commands can interact with the host uniformly.
/// </summary>
public interface IHost
{
    HostKind Kind { get; }

    /// <summary>Current location (cwd) in the host. Both hosts share this state.</summary>
    ItemPath CurrentLocation { get; set; }

    /// <summary>Selected items in the host. CLI: usually the last pipeline output. GUI: tree/list selection.</summary>
    IObservable<IReadOnlyList<IItem>> Selection { get; }

    /// <summary>Progress channel for long-running operations.</summary>
    IProgress<OperationProgress> Progress { get; }

    /// <summary>ServiceProvider exposing the host's DI scope (for advanced commands).</summary>
    IServiceProvider Services { get; }

    /// <summary>Write a line of output to the host (CLI: stdout; GUI: status/log panel).</summary>
    Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>Write a stream of items to the host's default renderer (CLI: table; GUI: list view).</summary>
    Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default);
}

public enum HostKind { Cli, Gui }

/// <summary>Progress for an in-flight operation.</summary>
public readonly record struct OperationProgress(
    long Completed,
    long? Total,
    string? Status,
    bool IsCompleted = false);
