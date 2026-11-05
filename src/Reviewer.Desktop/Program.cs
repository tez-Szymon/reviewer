using Avalonia;
using Reviewer.Desktop.Services;
using Velopack;

namespace Reviewer.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // First, as Velopack asks: the installer and updater run the app with hook arguments and expect it to exit.
        var velopack = VelopackApp.Build()
            // A downloaded update is applied only when the app asks, after stopping an engine that runs from its
            // folder; applying it at startup would replace the files under an engine kept running from the last run.
            .SetAutoApplyOnStartup(false);
        if (OperatingSystem.IsWindows())
        {
            // Uninstalling removes the Run entry with the app. macOS apps have no uninstaller to hook.
            velopack.OnBeforeUninstallFastCallback(_ => LoginItem.ForThisOs()?.Set(false));
        }

        velopack.Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
