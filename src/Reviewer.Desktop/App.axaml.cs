using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;
using Reviewer.Desktop.Views;
using Reviewer.Engine;

namespace Reviewer.Desktop;

internal sealed partial class App : Application
{
    /// <summary>
    /// The tray icon, on macOS and Windows. Linux trays vary too much to rely on before the Linux desktop build, so
    /// there closing the window still quits.
    /// </summary>
    private static readonly bool HasTray = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

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
                new DesktopSettings(Path.Combine(paths.Root, DesktopSettings.FileName)),
                new VelopackUpdater(),
                LoginItem.ForThisOs(),
                action => Dispatcher.UIThread.Post(action),
                async uri => await window.Launcher.LaunchUriAsync(uri),
                async folder => await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)));
            window.DataContext = model;
            ApplyAppearance(model.Appearance);
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.Appearance))
                {
                    ApplyAppearance(model.Appearance);
                }
            };
            var running = false;
            window.Opened += (_, _) =>
            {
                if (!running)
                {
                    running = true;
                    _ = model.RunAsync(start: true);
                }
            };

            // Started at login: only the tray and the engine, until the window is asked for.
            if (HasTray && desktop.Args?.Contains(LoginItem.Background) == true)
            {
                running = true;
                _ = model.RunAsync(start: true);
            }
            else
            {
                desktop.MainWindow = window;
            }

            model.StartUpdateChecks();

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

            if (HasTray)
            {
                AddTray(desktop, window, model);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Light or dark everywhere; Default follows the OS, switching when it does.</summary>
    private void ApplyAppearance(Appearance appearance) => RequestedThemeVariant = appearance switch
    {
        Appearance.Light => ThemeVariant.Light,
        Appearance.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>
    /// Closing the window hides it to the tray, so the engine the app follows (and may have started) goes on reviewing.
    /// The tray menu, or the app menu's Quit, quits.
    /// </summary>
    private void AddTray(IClassicDesktopStyleApplicationLifetime desktop, Window window, MainWindowViewModel model)
    {
        // The window hiding mustn't end the app; quitting is always asked for.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        void Show()
        {
            window.Show();
            window.WindowState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
            window.Activate();
        }

        window.Closing += (_, e) =>
        {
            // The close button or Cmd+W; quitting closes the window as ApplicationShutdown, and that's let through.
            if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        var keepRunning = new NativeMenuItem("Keep the engine running after quitting")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = model.KeepEngineRunning,
        };
        keepRunning.Click += (_, _) => model.KeepEngineRunning = !model.KeepEngineRunning;

        var startAtLogin = new NativeMenuItem("Start at login")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = model.StartAtLogin,
            IsVisible = model.CanStartAtLogin,
        };
        startAtLogin.Click += (_, _) => model.StartAtLogin = !model.StartAtLogin;

        var update = new NativeMenuItem("Restart to update") { IsVisible = model.Updates.IsReady };
        update.Click += (_, _) => model.RestartToUpdateCommand.Execute(null);

        // What the engine is doing, at the top of the menu; not clickable.
        var status = new NativeMenuItem(model.TrayText) { IsEnabled = false };

        var open = new NativeMenuItem("Open Reviewer");
        open.Click += (_, _) => Show();
        var quit = new NativeMenuItem("Quit Reviewer");
        quit.Click += (_, _) => desktop.TryShutdown();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri(
                OperatingSystem.IsMacOS() ? "avares://Reviewer.Desktop/Assets/tray-template.png" : "avares://Reviewer.Desktop/Assets/tray.png"))),
            ToolTipText = model.TrayText,
            Menu = [status, new NativeMenuItemSeparator(), open, update, new NativeMenuItemSeparator(), keepRunning, startAtLogin, new NativeMenuItemSeparator(), quit],
        };

        // The menu bar tints a template icon for light and dark, as it does its own.
        MacOSProperties.SetIsTemplateIcon(tray, true);
        tray.Clicked += (_, _) => Show();
        model.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.TrayText):
                    tray.ToolTipText = model.TrayText;
                    status.Header = model.TrayText;
                    break;
                case nameof(MainWindowViewModel.KeepEngineRunning):
                    keepRunning.IsChecked = model.KeepEngineRunning;
                    break;
                case nameof(MainWindowViewModel.StartAtLogin):
                    startAtLogin.IsChecked = model.StartAtLogin;
                    break;
            }
        };
        model.Updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdatesViewModel.IsReady))
            {
                update.Header = model.Updates.IsReady ? $"Restart to update to {model.Updates.ReadyVersion}" : "Restart to update";
                update.IsVisible = model.Updates.IsReady;
            }
        };
        TrayIcon.SetIcons(this, [tray]);

        // Clicking the Dock icon while the window is hidden brings it back, as Mac apps do.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    Show();
                }
            };
        }
    }
}
