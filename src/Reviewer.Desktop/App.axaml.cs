using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;
using Reviewer.Desktop.Views;
using Reviewer.Engine;

namespace Reviewer.Desktop;

internal sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = ReviewerPaths.Default;
            var client = new EngineClient(paths.Socket);
            var window = new MainWindow();
            var model = new MainWindowViewModel(
                client,
                new EngineConnection(client, new EngineLauncher(paths)),
                action => Dispatcher.UIThread.Post(action),
                async uri => await window.Launcher.LaunchUriAsync(uri),
                async folder => await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)));
            window.DataContext = model;
            window.Opened += (_, _) => _ = model.RunAsync(start: true);
            desktop.MainWindow = window;

            // Stop an engine the app started before the process goes; the lifetime waits for nothing on its own.
            var stopped = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (stopped)
                {
                    return;
                }

                e.Cancel = true;
                await model.ShutdownAsync();
                model.Dispose();
                client.Dispose();
                stopped = true;
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
