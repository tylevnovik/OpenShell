using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Host.Services;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.I18n;
using OpenShell.Sessions;

namespace OpenShell.Gui.Host;

internal sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Program.Services;
            var i18n = services.GetService<II18nService>();
            I18nAccessor.Instance = i18n;
            var guiHost = services.GetRequiredService<GuiHost>();
            var sessionService = services.GetRequiredService<ISessionService>();
            var sessionName = ResolveSessionName(desktop.Args);
            SessionTabsService? sessionTabs = null;
            var sessionLockAcquired = false;
            try
            {
                sessionService.LoadOrCreateAsync(sessionName).GetAwaiter().GetResult();
                sessionService.AcquireLockAsync(sessionName).GetAwaiter().GetResult();
                sessionLockAcquired = true;
                sessionTabs = services.GetRequiredService<SessionTabsService>();
            }
            catch
            {
                // 会话恢复失败不阻塞 GUI，继续使用当前进程目录。
            }

            var initialLocation = sessionService.Current?.State.CurrentLocation ?? guiHost.CurrentLocation;
            var mainVm = new MainViewModel(
                services.GetRequiredService<Providers.IProviderRegistry>(),
                services.GetRequiredService<Commands.ICommandRegistry>(),
                services.GetRequiredService<Operations.IOperationEngine>(),
                services.GetRequiredService<Gui.Abstractions.IDialogService>(),
                services.GetRequiredService<Operations.ITaskCenter>(),
                initialLocation,
                services.GetRequiredService<Errors.IErrorStream>(),
                guiHost.DispatchAsync,
                () => guiHost.CommandCancellation.Token,
                services.GetService<Clipboard.IClipboardService>(),
                services.GetService<History.IUndoService>(),
                services.GetService<Commands.Builtins.IQuickLookWindow>(),
                i18n,
                sessionTabs);

            mainVm.InitializeTabsAsync().GetAwaiter().GetResult();

            desktop.MainWindow = new Views.MainWindow(i18n)
            {
                DataContext = mainVm,
            };

            desktop.Exit += (_, _) =>
            {
                if (sessionTabs is null) return;
                try
                {
                    mainVm.FlushTabsToSession();
                    sessionTabs.FlushAsync().GetAwaiter().GetResult();
                }
                finally
                {
                    if (sessionLockAcquired)
                        sessionService.ReleaseLockAsync(sessionName).GetAwaiter().GetResult();
                }
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await guiHost.RunAsync();
                }
                catch (Exception ex)
                {
                    mainVm.CommandOutput = i18n?.Translate("gui.profile.failed", ex.Message) ?? $"profile execution failed: {ex.Message}";
                }
                finally
                {
                    mainVm.IsProfileLoading = false;
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string ResolveSessionName(string[]? args)
    {
        if (args is null) return "default";
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--session" && !string.IsNullOrWhiteSpace(args[index + 1]))
                return args[index + 1];
        }
        return "default";
    }
}
