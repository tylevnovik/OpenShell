using Avalonia;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using OpenShell.Commands;
using OpenShell.Gui.Abstractions;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.Providers.FileSystem;

namespace OpenShell.Gui.Host.Tests;

/// <summary>
/// Avalonia.Headless test infrastructure. Per ADR-0033 §3 (top of testing pyramid).
/// <para>
/// NOTE: The production <c>App</c> (OpenShell.Gui.Host.App) requires <c>Program.Services</c>
/// to be initialized with the full Generic Host before OnFrameworkInitializationCompleted can
/// create a MainWindow. To keep UI smoke tests lightweight, we use a dedicated <see cref="TestApp"/>
/// that loads the same FluentTheme but skips MainWindow creation in OnFrameworkInitializationCompleted.
/// Tests construct <see cref="MainWindow"/> directly with a manually-built
/// <see cref="MainViewModel"/> (real Core services + stub IDialogService).
/// </para>
/// </summary>
internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Tests construct MainWindow directly; no auto-creation needed.
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Static helper to build a headless Avalonia app for tests.
/// The <c>[AvaloniaFact]</c> attribute discovers <see cref="BuildAvaloniaApp"/> via reflection.
/// </summary>
public static class TestAppBuilder
{
    /// <summary>
    /// Builds a headless Avalonia AppBuilder for use with [AvaloniaFact] / [AvaloniaTheory].
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseReactiveUI()
            .WithInterFont();
    }

    /// <summary>
    /// Creates a <see cref="MainViewModel"/> with real Core services for UI testing.
    /// Uses a stub <see cref="IDialogService"/> (not called in smoke tests) and a real
    /// <see cref="InMemoryTaskCenter"/>. The initial location points to the system temp
    /// directory so that <c>RefreshCommand</c> does not throw.
    /// </summary>
    public static MainViewModel CreateMainViewModel()
    {
        var providers = new ProviderRegistry();
        providers.Register(new FileSystemProvider());

        var commands = new CommandRegistry();
        var operations = new OperationEngine(providers);
        var taskCenter = new InMemoryTaskCenter();
        var dialogs = new StubDialogService();
        var errors = new OpenShell.Errors.InMemoryErrorStream();

        var initialLocation = new ItemPath
        {
            Provider = "fs",
            InternalPath = System.IO.Path.GetTempPath().Replace('\\', '/'),
        };

        // 命令调度委托：测试中不实际调用 Core 命令调度（SubmitCommandInputCoreAsync 不在 smoke test 中触发）。
        // 提供一个 no-op stub 避免依赖完整 GuiHost。
        Func<string, CancellationToken, Task> dispatchLine = (_, _) => Task.CompletedTask;
        Func<CancellationToken> cancelTokenAccessor = () => CancellationToken.None;

        return new MainViewModel(providers, commands, operations, dialogs, taskCenter, initialLocation, errors, dispatchLine, cancelTokenAccessor);
    }

    /// <summary>
    /// Recursively finds all logical descendants of type <typeparamref name="T"/> under
    /// the given root. Traverses the Avalonia logical tree.
    /// </summary>
    public static List<T> FindDescendants<T>(ILogical root) where T : class
    {
        var results = new List<T>();
        FindDescendantsRecursive(root, results);
        return results;
    }

    private static void FindDescendantsRecursive<T>(ILogical node, List<T> results) where T : class
    {
        if (node is T t)
        {
            results.Add(t);
        }

        foreach (var child in node.LogicalChildren)
        {
            FindDescendantsRecursive(child, results);
        }
    }

    /// <summary>
    /// Pumps the Avalonia dispatcher so that deferred UI updates (via <c>Dispatcher.UIThread.Post</c>)
    /// are processed. Call this after changing a VM property that triggers a UI binding update.
    /// </summary>
    public static void PumpDispatcher()
    {
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>
/// Minimal stub <see cref="IDialogService"/> for UI smoke tests.
/// All methods return cancel/null — the dialog methods are not called during smoke tests.
/// </summary>
internal sealed class StubDialogService : IDialogService
{
    public Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions options, CancellationToken ct = default)
        => Task.FromResult(DialogResult.Cancel);

    public Task<IReadOnlyList<ItemPath>?> ShowOpenFileDialogAsync(FileDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ItemPath>?>(null);

    public Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<ItemPath?>(null);

    public Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<ItemPath?>(null);

    public Task<string?> ShowInputAsync(InputDialogOptions options, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct = default)
        => Task.FromResult<T?>(default);
}
