using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.I18n;

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
            var mainVm = new MainViewModel(
                services.GetRequiredService<Providers.IProviderRegistry>(),
                services.GetRequiredService<Commands.ICommandRegistry>(),
                services.GetRequiredService<Operations.IOperationEngine>(),
                services.GetRequiredService<Gui.Abstractions.IDialogService>(),
                services.GetRequiredService<Operations.ITaskCenter>(),
                guiHost.CurrentLocation,
                services.GetRequiredService<Errors.IErrorStream>(),
                guiHost.DispatchAsync,
                () => guiHost.CommandCancellation.Token,
                services.GetService<Clipboard.IClipboardService>(),
                services.GetService<History.IUndoService>(),
                services.GetService<Commands.Builtins.IQuickLookWindow>());

            desktop.MainWindow = new Views.MainWindow
            {
                DataContext = mainVm,
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
}
